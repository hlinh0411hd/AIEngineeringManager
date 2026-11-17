"""Manager-owned CLI around the TrailerForge source; installs nothing.

inspect/export read or render a saved TrailerForge project. generate runs TrailerForge's own AI video
generator (aigen_jobs, the same code as its "AI Video" workspace) without opening its window: the
script is written by the AI CLI configured in TrailerForge, voice and media come from its configured
providers. The manager counts it as a model call and runs it only after approval. TrailerForge's own
script-review pause is passed (the manager reviews results itself); any paid service or budget raise
TrailerForge asks about is refused, so it falls back to free providers.
"""
import argparse
import json
import sys
import time
from pathlib import Path

GENERATE_KEYS = ('mode', 'topic', 'brief', 'script', 'title', 'captions', 'duration', 'format', 'language', 'style', 'llm', 'voice', 'voice_id',
                 'voice_rate', 'subtitle_style', 'music_mode', 'music_volume', 'media_pref', 'still_motion')
POLL_SECONDS = 2


def emit(value, result_file):
    """Return the result to the manager. With --result-file the JSON goes to that file only, so renderer
    logs on stdout/stderr can neither hide nor truncate it; without it, a single JSON line is printed."""
    text = json.dumps(value, ensure_ascii=False)
    if not result_file:
        print(text, flush=True)
        return
    target = Path(result_file)
    temp = target.with_name(target.name + '.tmp')
    temp.write_text(text, encoding='utf-8')
    temp.replace(target)


def inside(path, root: Path) -> Path:
    resolved = Path(path).resolve(strict=True)
    if not resolved.is_relative_to(root):
        raise ValueError('TrailerForge wrote outside the manager run folder.')
    return resolved


def generate(source: Path, root: Path, output_dir: Path, request: dict) -> dict:
    sys.path.insert(0, str(source))
    import aigen_store as store
    from aigen_jobs import manager
    params = {k: request[k] for k in GENERATE_KEYS if request.get(k) is not None}
    params.setdefault('mode', 'text')
    if request.get('asset_folder'):
        folder = Path(str(request['asset_folder'])).resolve(strict=True)
        if not folder.is_relative_to(root) or not folder.is_dir():
            raise ValueError('asset_folder must be a folder inside the approved workspace.')
        params['asset_folder'] = str(folder)
    if isinstance(request.get('media'), dict):
        # Only free sources may be switched on from a task; paid ones stay as TrailerForge has them.
        cfg = store.load_config()
        enabled = dict(cfg['media']['enabled'])
        for name in ('local', 'pexels', 'pixabay', 'commons', 'ai_image'):
            if isinstance(request['media'].get(name), bool):
                enabled[name] = request['media'][name]
        params['media_enabled'] = enabled
    params.update({'review': False, 'budget': 0, 'output_root': str(output_dir)})
    manager.start()
    job = manager.create(params, [])
    ident = job['id']
    exporting = False
    while True:
        time.sleep(POLL_SECONDS)
        job = manager.get(ident)
        status = job['status']
        if status == 'waiting_approval':
            pending = job['approvals'].get('pending') or {}
            manager.respond(ident, 'approve' if pending.get('kind') == 'review' else 'deny')
            continue
        if status in ('failed', 'cancelled', 'resumable'):
            last = [entry['message'] for entry in job.get('logs', [])[-5:]]
            raise ValueError('TrailerForge job ' + status + ': ' + (job.get('error') or '') + (' | ' + ' | '.join(last) if last else ''))
        if status != 'completed':
            continue
        result = job.get('result') or {}
        if result.get('export') and Path(result['export']).is_file():
            break
        if exporting:
            raise ValueError('TrailerForge finished without an exported MP4.')
        manager.request_export(ident)
        exporting = True
    detail = manager.detail(ident)
    scenes = (job.get('plan') or {}).get('scenes', [])
    media = [s['status']['media'] for s in scenes]
    return {
        'artifactPath': str(inside(result['export'], output_dir)),
        'projectFolder': str(inside(job['folder'], output_dir)),
        'title': (job.get('plan') or {}).get('title'),
        'scenes': len(scenes),
        'scenesWithMedia': sum(1 for m in media if m == 'ready'),
        'scenesWithFallback': sum(1 for m in media if m in ('fallback', 'failed')),
        'unavailable': job.get('unavailable') or {},
        'warnings': (job.get('warnings') or [])[-8:],
        'costUsd': (job.get('costs') or {}).get('total', 0),
        'progress': detail.get('progress'),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', required=True)
    parser.add_argument('--root', required=True)
    parser.add_argument('--operation', choices=['inspect', 'export', 'generate'], required=True)
    parser.add_argument('--output-dir')
    parser.add_argument('--result-file')
    args = parser.parse_args()
    source = Path(args.source).resolve(strict=True)
    root = Path(args.root).resolve(strict=True)
    request = json.loads(sys.stdin.read(1000000) or '{}')
    if args.operation == 'generate':
        if not isinstance(request, dict):
            raise ValueError('Tool input must be a JSON object.')
        if not args.output_dir:
            raise ValueError('Manager output folder is missing.')
        emit(generate(source, root, Path(args.output_dir).resolve(strict=True), request), args.result_file)
        return
    if not isinstance(request, dict) or not isinstance(request.get('projectFolder'), str):
        raise ValueError('projectFolder is required: the saved TrailerForge project folder inside the workspace.')
    folder = Path(request['projectFolder']).resolve(strict=True)
    if not folder.is_relative_to(root) or not folder.is_dir():
        raise ValueError('Project folder is outside the approved workspace root.')
    spec = folder / 'project.json'
    if spec.stat().st_size > 5000000:
        raise ValueError('Saved project exceeds 5 MB.')
    project = json.loads(spec.read_text(encoding='utf-8-sig'))
    if args.operation == 'inspect':
        emit({'projectFolder': str(folder), 'project': project}, args.result_file)
        return
    if not (source / 'editor_core.py').is_file() or not (source / 'export_latest.py').is_file():
        raise ValueError('Unsupported TrailerForge source layout.')
    sys.path.insert(0, str(source))
    from engine import Runner
    from project_storage import prepare
    from export_latest import export_latest
    prepared = prepare(project, folder)
    runner = Runner(log=lambda text: print(text, file=sys.stderr, flush=True))
    artifact = export_latest(prepared, folder, runner)
    emit({'artifactPath': str(Path(artifact).resolve()), 'projectFolder': str(folder)}, args.result_file)


if __name__ == '__main__':
    # The manager exchanges UTF-8; piped Windows streams default to the ANSI code page, which cannot
    # carry non-ASCII project names or paths.
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        stream.reconfigure(encoding='utf-8', errors='replace' if stream is not sys.stdin else 'strict')
    try:
        main()
    except Exception as error:
        print(f'{type(error).__name__}: {error}', file=sys.stderr)
        raise SystemExit(1)
