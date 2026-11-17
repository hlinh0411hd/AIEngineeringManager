"""Manager-owned CLI around the inspected ZapBop Music Producer source.

inspect/export call the engine functions ZapBop's own UI uses (pro_engine.Project.load,
render_project, audio_engine.write_wav). sfx/compose/render call ZapBop's own API module
(zapbop_api.call, the same entry point as its HTTP server and CLI; see ZapBop's API.md). compose
runs ZapBop's AI composer, which drives an AI CLI on this machine: the manager counts it as a model
call and only runs it after approval. The bridge installs nothing, opens no audio device and writes
only into the output folder the manager passes.
"""
import argparse
import json
import re
import sys
from pathlib import Path

MAX_PROJECT_BYTES = 5_000_000
MAX_SECONDS = 15 * 60  # manager-side memory cap; ZapBop's UI has none
REQUIRED_FILES = ('main.py', 'pro_engine.py', 'audio_engine.py')
API_OPERATIONS = ('sfx', 'compose', 'render')
SFX_KEYS = ('kind', 'pitch', 'decay', 'distortion', 'seed', 'slide', 'vibrato', 'attack', 'tone', 'echo')
COMPOSE_KEYS = ('description', 'provider', 'model', 'bpm', 'bars', 'seconds', 'root', 'scale', 'genre', 'mood', 'density')
PROVIDERS = ('Claude', 'Codex', 'Qoder')
FORMATS = {'WAV': '.wav', 'OGG': '.ogg'}


def load_engine(source: Path):
    if not all((source / name).is_file() for name in REQUIRED_FILES):
        raise ValueError('Unsupported ZapBop source layout: expected main.py, pro_engine.py and audio_engine.py.')
    sys.path.insert(0, str(source))
    import audio_engine  # noqa: E402  (numpy + sounddevice from ZapBop's own environment)
    import pro_engine  # noqa: E402
    return audio_engine, pro_engine


def project_path(request: dict, root: Path) -> Path:
    raw = request.get('projectFile')
    if not isinstance(raw, str) or not raw.strip():
        raise ValueError('projectFile is required: the saved ZapBop project (.json) inside the allowed workspace.')
    path = Path(raw).resolve(strict=True)
    if not path.is_relative_to(root) or not path.is_file():
        raise ValueError('Project file is outside the approved workspace root.')
    if path.suffix.lower() != '.json':
        raise ValueError('ZapBop projects are saved as .json files.')
    if path.stat().st_size > MAX_PROJECT_BYTES:
        raise ValueError('Saved project exceeds 5 MB.')
    return path


def summary(project, data: dict, audio_engine, path: Path) -> dict:
    bars = max([len(layer['bars']) for layer in data['layers']] + [1])
    seconds = bars * audio_engine.STEPS * (60.0 / project.bpm / 4)
    return {
        'projectFile': str(path),
        'title': project.title,
        'bpm': project.bpm,
        'root': project.root,
        'scale': project.scale,
        'bars': project.bar_count,
        'durationSeconds': round(seconds, 3),
        'instruments': [i.sound for i in project.instruments],
        'patterns': len(project.patterns),
        'layers': [{'name': l.name, 'kind': l.kind, 'muted': l.muted, 'bars': len(l.bars)} for l in project.layers],
        'sampleRate': audio_engine.SAMPLE_RATE,
    }, seconds


def load_api(source: Path):
    if not (source / 'zapbop_api.py').is_file():
        raise ValueError('This ZapBop version has no API (zapbop_api.py is missing); update ZapBop to use sfx, compose and render.')
    import zapbop_api  # noqa: E402  (source is already on sys.path)
    return zapbop_api


def api_call(zb, method: str, **params) -> dict:
    try:
        return zb.call(method, **params)
    except zb.ApiError as error:
        raise ValueError(f'ZapBop {method}: {error} ({error.code})') from None


def file_stem(request: dict, fallback: str) -> str:
    raw = request.get('name') or request.get('title') or fallback
    return re.sub(r'[^A-Za-z0-9_-]+', '_', str(raw)).strip('_')[:60] or fallback


def audio_format(request: dict) -> str:
    label = str(request.get('format') or 'WAV').upper()
    if label not in FORMATS:
        raise ValueError('format must be WAV or OGG.')
    return label


def clamp_sfx(zb, params: dict) -> list:
    """Pull shaping values into ZapBop's own ranges instead of failing the whole request (seen live:
    decay 0.06 for a UI tap, refused three times). Returns what was changed."""
    ranges = (api_call(zb, 'info.sfx').get('ranges') or {})
    changed = []
    for key, bounds in ranges.items():
        if key in params and isinstance(params[key], (int, float)) and len(bounds) == 2:
            value = min(max(float(params[key]), float(bounds[0])), float(bounds[1]))
            if value != params[key]:
                changed.append(f'{key} {params[key]} -> {value} (ZapBop allows {bounds[0]}-{bounds[1]})')
                params[key] = value
    return changed


def to_ogg(wav: Path, target: Path) -> Path:
    """Write the OGG ourselves in small blocks. libsndfile's Vorbis encoder overflows the stack when a
    whole song is written in one call (seen live: every 75-90 s compose render died with 0xC00000FD and
    left a 4 KB OGG header); short effects were fine, which hid it."""
    import soundfile  # ZapBop's own environment
    with soundfile.SoundFile(str(wav)) as source, soundfile.SoundFile(str(target), 'w', samplerate=source.samplerate,
                                                                    channels=source.channels, format='OGG', subtype='VORBIS') as sink:
        for block in source.blocks(blocksize=8192):
            sink.write(block)
    wav.unlink()
    return target


def render_to(zb, method: str, params: dict, target: Path, label: str) -> dict:
    """Render one file. OGG goes through a WAV render and the block-wise encoder above."""
    if label != 'OGG':
        return api_call(zb, method, **params, out=str(target), format=label)
    wav = target.with_name('.' + target.stem + '.render.wav')
    result = api_call(zb, method, **params, out=str(wav), format='WAV')
    result['path'] = str(to_ogg(Path(result['path']), target))
    return result


def new_target(output_dir: Path, stem: str, label: str) -> Path:
    target = output_dir / (stem + FORMATS[label])
    if target.exists():
        raise ValueError('Output file already exists for this request; create a new request instead of overwriting.')
    return target


def inside(path: str, folder: Path) -> Path:
    resolved = Path(path).resolve(strict=True)
    if not resolved.is_relative_to(folder):
        raise ValueError('ZapBop wrote outside the manager run folder.')
    return resolved


def readable_project(request: dict, roots: list) -> Path:
    raw = request.get('projectFile')
    if not isinstance(raw, str) or not raw.strip():
        raise ValueError('projectFile is required: a saved ZapBop project (.json), or the projectFile a compose step returned.')
    path = Path(raw).resolve(strict=True)
    if not any(path.is_relative_to(root) for root in roots) or path.suffix.lower() != '.json' or not path.is_file():
        raise ValueError('projectFile must be a .json inside the approved workspace or a manager tool-run folder.')
    if path.stat().st_size > MAX_PROJECT_BYTES:
        raise ValueError('Saved project exceeds 5 MB.')
    return path


def run_api(operation: str, source: Path, request: dict, output_dir: Path, roots: list) -> dict:
    zb = load_api(source)
    if operation == 'sfx':
        params = {k: request[k] for k in SFX_KEYS if request.get(k) is not None}
        if not params.get('kind'):
            raise ValueError('kind is required (a ZapBop SFX preset such as Coin or Jump, or Random with a seed).')
        label = audio_format(request)
        target = new_target(output_dir, file_stem(request, str(params['kind'])), label)
        adjusted = clamp_sfx(zb, params)
        result = render_to(zb, 'sfx.render', params, target, label)
        return {'artifactPath': str(inside(result['path'], output_dir)), 'seconds': result['seconds'],
                'channels': result['channels'], 'sampleRate': result['sample_rate'], 'params': result.get('params'),
                'adjusted': adjusted or None}
    if operation == 'compose':
        params = {k: request[k] for k in COMPOSE_KEYS if request.get(k) is not None}
        if not str(params.get('description') or '').strip():
            raise ValueError('description is required: what the music is for and how it should sound.')
        if params.get('provider') is not None and params['provider'] not in PROVIDERS:
            raise ValueError('provider must be Claude, Codex or Qoder.')
        if float(params.get('seconds') or 0) > MAX_SECONDS:
            raise ValueError(f'seconds must be at most {MAX_SECONDS}.')
        mode = request.get('mode') or 'loop'
        if mode not in ('loop', 'once'):
            raise ValueError('mode must be loop or once.')
        stem = file_stem(request, 'zapbop_song')
        label = audio_format(request)
        project_file = output_dir / (stem + '.json')
        target = new_target(output_dir, stem, label)
        composed = api_call(zb, 'ai.compose_project', **params, consent=True, save=str(project_file))
        rendered = render_to(zb, 'project.render', {'project': composed['project'], 'mode': mode}, target, label)
        summary = composed.get('summary') or {}
        return {'projectFile': str(inside(composed['saved'], output_dir)), 'artifactPath': str(inside(rendered['path'], output_dir)),
                'seconds': rendered['seconds'], 'channels': rendered['channels'], 'sampleRate': rendered['sample_rate'],
                'title': summary.get('title'), 'bpm': summary.get('bpm'), 'root': summary.get('root'), 'scale': summary.get('scale'),
                'partialError': composed.get('partial_error')}
    path = readable_project(request, roots)
    mode = request.get('mode') or 'once'
    if mode not in ('loop', 'once', 'stems', 'variants'):
        raise ValueError('mode must be loop, once, stems or variants.')
    label = audio_format(request)
    params = {'project': str(path), 'mode': mode}
    if request.get('variant') is not None:
        params['variant'] = int(request['variant'])
    if mode in ('stems', 'variants'):
        result = api_call(zb, 'project.render', **params, format='WAV', out_dir=str(output_dir))
        files = [inside(f, output_dir) for f in result.get('files', [])]
        if label == 'OGG':
            files = [to_ogg(f, f.with_suffix('.ogg')) for f in files]
        files = [str(f) for f in files]
        if not files:
            raise ValueError('ZapBop rendered no files (every layer muted or empty?).')
        return {'projectFile': str(path), 'artifactPath': files[0], 'artifactPaths': files}
    target = new_target(output_dir, file_stem(request, path.stem), label)
    result = render_to(zb, 'project.render', params, target, label)
    return {'projectFile': str(path), 'artifactPath': str(inside(result['path'], output_dir)), 'seconds': result['seconds'],
            'channels': result['channels'], 'sampleRate': result['sample_rate']}


def emit(value, result_file):
    """Return the result to the manager. With --result-file the JSON goes to that file only, so engine
    logs on stdout/stderr can neither hide nor truncate it; without it, a single JSON line is printed."""
    text = json.dumps(value, ensure_ascii=False)
    if not result_file:
        print(text, flush=True)
        return
    target = Path(result_file)
    temp = target.with_name(target.name + '.tmp')
    temp.write_text(text, encoding='utf-8')
    temp.replace(target)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', required=True)
    parser.add_argument('--root')
    parser.add_argument('--output-dir')
    parser.add_argument('--operation', choices=['status', 'inspect', 'export', *API_OPERATIONS], required=True)
    parser.add_argument('--artifacts-root')
    parser.add_argument('--result-file')
    args = parser.parse_args()
    source = Path(args.source).resolve(strict=True)
    audio_engine, pro_engine = load_engine(source)
    if args.operation == 'status':
        import numpy
        emit({'source': str(source), 'projectFormat': 'zapbop-pro', 'numpy': numpy.__version__,
              'sounds': list(pro_engine.SOUND_NAMES), 'sampleRate': audio_engine.SAMPLE_RATE,
              'api': (source / 'zapbop_api.py').is_file(), 'sfxKinds': list(getattr(audio_engine, 'PRESETS', {}))}, args.result_file)
        return

    if args.operation in API_OPERATIONS:
        if not args.output_dir:
            raise ValueError('Manager output folder is missing.')
        request = json.loads(sys.stdin.read(1_000_000) or '{}')
        if not isinstance(request, dict):
            raise ValueError('Tool input must be a JSON object.')
        roots = [Path(r).resolve(strict=True) for r in (args.root, args.artifacts_root) if r]
        emit(run_api(args.operation, source, request, Path(args.output_dir).resolve(strict=True), roots), args.result_file)
        return

    if not args.root:
        raise ValueError('Choose the allowed ZapBop project workspace root.')
    root = Path(args.root).resolve(strict=True)
    request = json.loads(sys.stdin.read(1_000_000) or '{}')
    if not isinstance(request, dict):
        raise ValueError('Tool input must be a JSON object.')
    path = project_path(request, root)
    project = pro_engine.Project.load(path)  # ZapBop's own strict validation
    data = project.to_dict()
    info, seconds = summary(project, data, audio_engine, path)
    if args.operation == 'inspect':
        emit(info, args.result_file)
        return

    if project.bar_count == 0:
        raise ValueError('The song is empty: place at least one pattern in the arrangement before exporting.')
    if seconds > MAX_SECONDS:
        raise ValueError(f'Song is {seconds:.0f} s; the connector exports at most {MAX_SECONDS} s.')
    if not args.output_dir:
        raise ValueError('Manager output folder is missing.')
    output_dir = Path(args.output_dir).resolve(strict=True)
    stem = re.sub(r'[^A-Za-z0-9_-]+', '_', path.stem).strip('_')[:60] or 'zapbop_song'
    artifact = output_dir / f'{stem}_{project.bpm}bpm.wav'
    if artifact.exists():
        raise ValueError('Output file already exists for this request; create a new request instead of overwriting.')
    audio = pro_engine.render_project(data)
    audio_engine.write_wav(artifact, audio)
    info.update({'artifactPath': str(artifact), 'channels': int(audio.shape[1]) if audio.ndim == 2 else 1,
                 'frames': int(audio.shape[0]), 'bytes': artifact.stat().st_size})
    emit(info, args.result_file)


if __name__ == '__main__':
    # The manager exchanges UTF-8; piped Windows streams default to the ANSI code page, which
    # cannot carry ZapBop's Vietnamese layer names or non-ASCII paths. (-I ignores PYTHONIOENCODING.)
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        stream.reconfigure(encoding='utf-8', errors='replace' if stream is sys.stderr else 'strict')
    try:
        main()
    except Exception as error:
        print(f'{type(error).__name__}: {error}', file=sys.stderr)
        raise SystemExit(1)
