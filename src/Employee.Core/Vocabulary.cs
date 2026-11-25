namespace Employee.Core;

// Keyword lists used to read a user's request (Vietnamese without diacritics, as produced by
// TaskIntent.Normalize, plus English). They decide difficulty, work type and whether to plan;
// keep every list here so wording can be tuned in one place. Intent classification lists live in
// TaskIntent; per-work-type signals live with WorkCapabilities.
public static class Vocabulary
{
    // Whole subsystems or migrations: difficulty at least 78.
    public static readonly string[] HeavyWork = ["kien truc", "architecture", "schema migration", "migrate database", "database migration", "di tru du lieu", "refactor toan bo", "cross system", "across multiple modules", "nhieu module", "tinh nang lon", "major feature", "new subsystem", "tu dau", "from scratch", "end to end", "tich hop nhieu", "concurrency", "dong thoi"];
    // Small local edits: difficulty at most 25.
    public static readonly string[] LightWork = ["doi icon", "change icon", "doi mau", "change color", "doi ten", "rename", "chinh padding", "adjust padding", "chinh margin", "adjust margin", "sua chinh ta", "fix typo", "doi nhan", "change label", "mot dong", "one line", "single file", "mot file", "doi vi tri nut", "move button", "them nut", "add button", "an nut", "hide button", "hien nut", "show button", "doi chu", "change text"];
    // Reasoning-heavy or risky work: +5 difficulty each (max +15).
    public static readonly string[] HarderWork = ["toi uu", "optimize", "performance", "hieu nang", "bao mat", "security", "debug", "race condition", "memory leak", "thuat toan", "algorithm", "multiplayer", "network", "dong bo", "sync", "save system", "luu game", "shader", "physics", "vat ly", "ai enemy"];
    // Requests to produce images.
    public static readonly string[] ImageGeneration = ["gen anh", "tao anh", "tao hinh anh", "ve anh", "ve art", "ve png", "gen art", "tao art", "create image", "create images", "generate image", "generate images", "imagegen", "generated art", "generated assets", "generate sprites", "tao sprite"];
    // Raw (accented) phrases that make a request worth planning as several steps.
    public static readonly string[] NeedsPlanning = ["nhiều bước", "toàn bộ", "đủ các phần", "tất cả các phần", "đầy đủ", "làm tiếp game", "làm tiếp app", "làm tiếp tool", "phát triển tiếp", "hoàn thiện game", "hoàn thiện app", "và sau đó", "hệ thống", "xây dựng", "triển khai", "nghiên cứu", "lên kế hoạch", "multiple steps", "end to end", "continue the game", "finish the game", "full game", "all parts"];
}
