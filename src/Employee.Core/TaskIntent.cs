using System.Globalization;
using System.Text;

namespace Employee.Core;
public static class TaskIntent
{
    public static string Normalize(string value)
    {
        var result = new StringBuilder();
        var space = true;
        foreach (var character in value.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            var c = character == 'đ' ? 'd' : character;
            if (char.IsLetterOrDigit(c))
            {
                result.Append(c);
                space = false;
            }
            else if (!space)
            {
                result.Append(' ');
                space = true;
            }
        }

        return result.ToString().Trim();
    }

    private static bool Has(string normalized, params string[] terms) => terms.Any(term => (" " + normalized + " ").Contains(" " + Normalize(term) + " ", StringComparison.Ordinal));
    // A short comment that only asks to run the task again ("lại đi", "làm lại", "again"): the
    // request is the original task, not the comment's two words (seen live: "lai di" became a
    // single step that handled only the comment and planned nothing).
    public static bool IsRedo(string comment)
    {
        var text = Normalize(comment);
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8 && Has(text,
            "lai di", "lam lai", "chay lai", "thu lai", "run lai", "lam tiep", "tiep di", "tiep tuc", "chay tiep", "lam lai di", "lam lai tu dau",
            "again", "do it again", "try again", "retry", "redo", "rerun", "run again", "continue", "keep going");
    }

    // A short comment that only accepts what was shown ("ok", "được rồi", "duyệt", "làm tiếp"), with no
    // change in it ("ok nhưng sửa màu" is a change). Used to sign off a milestone by comment.
    public static bool IsApproval(string comment)
    {
        var text = Normalize(comment);
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is > 0 and <= 6
            && Has(text, "ok", "oke", "okay", "on", "on roi", "duoc", "duoc roi", "dong y", "duyet", "confirm", "approve", "approved", "lgtm", "looks good", "good", "tot", "tot roi",
                "tiep di", "lam tiep", "tiep tuc", "next", "continue", "sang milestone tiep", "chay tiep")
            && !Has(text, "nhung", "but", "sua", "fix", "doi", "change", "chua", "khong", "not", "them", "bot", "lai", "xau", "sai", "wrong", "bad", "redo");
    }

    public static bool IsReadOnly(string request)
    {
        var text = Normalize(request);
        // A reported inability to edit is not an instruction forbidding edits.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\bkhong (sua|chinh sua|thay doi) (duoc|gi|xong|thanh cong)\b", " ");
        return Has(text, "chỉ đọc", "không sửa", "không thay đổi", "không chỉnh sửa", "chỉ phân tích", "chỉ kiểm tra", "chỉ xem", "chỉ review", "read only", "do not edit", "don't modify", "do not change", "analysis only", "review only", "without editing");
    }

    // "Make a game / app / tool": building a whole new thing, which is planned as milestones and
    // verified by running it, however short the request is.
    private static readonly System.Text.RegularExpressions.Regex NewBuild = new(
        @"\b(lam|tao|xay dung|phat trien|viet|lap trinh|make|build|create|develop|write|code)" +
        @"(?:\s+(?:1|mot|cai|con|ra|cho toi|cho minh|giup toi|giup minh|a|an|the|me|us|new|moi|small|simple|nho|mini|don gian|hoan chinh|complete|full|playable))*" +
        @"\s+(game|tro choi|app|ung dung|website|web app|trang web|tool|cong cu|phan mem|prototype|chuong trinh)\b",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public static bool IsNewBuild(string request) => NewBuild.IsMatch(Normalize(request));

    // "Research … and change/improve the project after it": the research is a means; what the user
    // wants is the project changed. Seen live: "nghiên cứu rồi chỉnh sửa game dựa theo các game hot"
    // produced a document and a few readability tweaks.
    public static bool IsResearchDriven(string request)
    {
        var text = Normalize(request);
        return Has(text, "nghiên cứu", "tham khảo", "học theo", "dựa theo", "dựa trên các", "theo các game", "theo các app", "phân tích thị trường", "research", "based on", "inspired by", "like the top", "market")
            && Has(text, "sửa", "chỉnh", "chỉnh sửa", "cải thiện", "nâng cấp", "làm lại", "thay đổi", "cập nhật", "áp dụng", "update", "improve", "change", "redesign", "rework", "upgrade", "apply", "make");
    }

    public static bool IsTextDeliverable(string request) => Has(Normalize(request), "viết caption", "soạn caption", "viết bài đăng", "soạn bài đăng", "viết email", "soạn email", "viết kịch bản", "viết mô tả sản phẩm", "dịch đoạn văn sau", "dịch văn bản sau", "tóm tắt văn bản sau", "write a caption", "write a social post", "draft an email", "write an email", "write a script", "translate the following", "summarize the following");
    public static bool RequiresImplementation(string request, ProjectInfo? project = null)
    {
        var text = Normalize(request);
        var explicitChange = Has(text, "sửa tool", "sửa app", "sửa ứng dụng", "sửa code", "sửa mã", "sửa lỗi", "fix lỗi", "vá lỗi", "khắc phục lỗi", "thêm tính năng", "bổ sung tính năng", "cập nhật tính năng", "triển khai tính năng", "tích hợp tính năng", "cập nhật giao diện", "đổi giao diện", "làm lại giao diện", "tối ưu code", "tái cấu trúc", "viết code", "viết mã", "tạo hệ thống", "xây dựng hệ thống", "xây dựng ứng dụng", "làm tiếp game", "làm tiếp app", "làm tiếp tool", "phát triển tiếp", "hoàn thiện game", "hoàn thiện app", "update ui", "update the ui", "update tool", "implement", "refactor", "fix bug", "fix the bug", "bugfix", "create an app", "build an app", "add support", "continue the game", "finish the game");
        if (explicitChange)
            return true;
        if (IsTextDeliverable(request) || Has(text, "nghiên cứu thông tin", "tìm thông tin trên mạng", "research information", "compare public information"))
            return false;
        if (Has(text, "task", "project", "access", "quyền", "permission", "resume") && Has(text, "bấm đổi", "khi bấm", "khi nhấn", "chạy lại từ đầu", "run lại", "run resume", "lưu quyền", "chọn project", "đổi project", "thay đổi quyền", "sửa quyền", "đổi quyền"))
            return true;
        if (Has(text, "task", "project", "type", "access", "quyền", "tool") && Has(text, "khi điền", "khi tạo", "khi nhập", "khi giao", "theo design", "theo thiết kế") && Has(text, "tự động", "phân loại", "nhận diện", "chọn"))
            return true;
        if (Has(text, "bug", "lỗi", "build", "biên dịch", "compile") && Has(text, "sửa đi", "sửa lại", "khắc phục", "fix it", "fix this"))
            return true;
        var surface = Has(text, "giao diện", "chức năng", "tính năng", "luồng", "workflow", "bài tập", "ui", "gui", "ux", "task list", "danh sách task", "danh sách nhiệm vụ", "nút", "checkbox", "setting", "settings", "cài đặt", "menu", "tab", "màn hình", "ô nhập", "textbox", "dropdown", "combobox", "bộ lọc", "sắp xếp", "phân trang", "cơ sở dữ liệu", "database", "schema", "migration", "api", "endpoint", "đăng nhập", "đăng ký", "lưu trữ", "checkpoint", "export", "import", "xuất file", "nhập file", "render", "animation", "sprite", "va chạm", "vùng chạm", "độ khó", "phần thưởng", "icon", "collision", "inventory", "quest", "nhiệm vụ hằng ngày", "âm thanh", "phụ đề", "subtitle");
        var change = Has(text, "bỏ", "thêm", "bổ sung", "ẩn", "hiện", "đổi", "sửa", "chỉnh", "chỉnh sửa", "cập nhật", "nâng cấp", "tối ưu", "khắc phục", "tích hợp", "kết nối", "hỗ trợ", "thay thế", "thay lại", "tăng", "giảm", "xóa", "xoá", "remove", "add", "hide", "change", "update", "improve", "optimize", "replace", "delete", "integrate", "enable", "disable");
        if (surface && change)
            return true;
        if (surface && Has(text, "bị sát", "sát nhau", "đè lên nhau", "chồng lên nhau", "tràn khung", "lệch", "bị che", "không hoạt động", "không bấm được", "overlapping", "overlap", "clipped", "broken layout") && !Has(text, "chỉ giải thích", "giải thích vì sao", "explain only"))
            return true;
        var names = project == null ? Array.Empty<string>() : new[]
        {
            project.Name,
            Path.GetFileName(project.Root.TrimEnd('\\', '/')) ?? ""
        }.Concat(project.Aliases.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray();
        static string Compact(string value) => Normalize(value).Replace(" ", "");
        var compact = Compact(request);
        var scoped = Has(text, "trong app", "trong ứng dụng", "trong game", "trong project", "in the app", "in game", "in this project") || names.Select(Compact).Any(n => n.Length >= 3 && compact.Contains(n, StringComparison.Ordinal));
        var feature = surface || Has(text, "phần", "mục", "chức năng", "tính năng", "bài tập", "chế độ", "luồng", "hệ thống", "feature", "exercise", "screen", "input", "workflow");
        var desired = change || Has(text, "muốn", "mong muốn", "cần", "hãy", "cho phép", "thay vì", "chứ không", "phải", "nên", "i want", "should", "instead of", "must", "allow");
        var behavior = Has(text, "nghe", "nhập", "gõ", "đánh", "hiển thị", "chọn", "lưu", "trả lời", "bấm", "nhấn", "đọc", "chạy", "mở", "đóng", "tải", "phát", "kéo thả", "tự động", "submit", "display", "type", "enter", "save", "answer", "show", "listen", "select", "load", "play", "run", "open", "close", "automatically");
        return scoped && feature && desired && behavior;
    }

    public static bool IsExplicitDataTask(string request, WorkKind kind)
    {
        var text = Normalize(request);
        var operation = kind == WorkKind.Classification ? Has(text, "phân loại", "phân nhóm", "gán nhãn", "classify", "categorize", "categorise", "label these") : Has(text, "trích xuất", "extract", "lấy các trường", "lấy thông tin từ", "tách dữ liệu từ");
        return operation && !RequiresImplementation(request);
    }

    public static bool TryResolve(string request, out WorkKind kind, out AccessLevel access, ProjectInfo? project = null)
    {
        var text = Normalize(request);
        kind = WorkKind.Coding;
        access = AccessLevel.ReadProject;
        // Only the user's own request is read as a new build; plan steps that mention the goal
        // ("research what makes the game fun") keep their own kind.
        if (RequiresImplementation(request, project) || IsNewBuild(request))
        {
            access = IsReadOnly(request) ? AccessLevel.ReadProject : AccessLevel.WriteProject;
            return true;
        }

        if (Has(text, "check ollama", "kiểm tra ollama", "kiểm tra model ollama", "review code", "code review", "review the code", "kiểm tra code", "kiểm tra mã", "rà soát code", "rà soát mã", "đọc code", "phân tích code", "tìm nguyên nhân lỗi", "điều tra lỗi", "kiểm tra logic", "inspect code", "debug this", "find the root cause"))
        {
            kind = WorkKind.Review;
            return true;
        }

        if (IsTextDeliverable(request))
        {
            kind = WorkKind.Content;
            access = AccessLevel.TextOnly;
            return true;
        }

        if (Has(text, "nghiên cứu thông tin", "tìm thông tin trên mạng", "tìm tài liệu", "so sánh các công cụ", "research information", "compare public information", "research online", "find documentation"))
        {
            kind = WorkKind.Research;
            access = AccessLevel.TextOnly;
            return true;
        }

        if (IsExplicitDataTask(request, WorkKind.Classification))
        {
            kind = WorkKind.Classification;
            access = AccessLevel.TextOnly;
            return true;
        }

        if (IsExplicitDataTask(request, WorkKind.Extraction))
        {
            kind = WorkKind.Extraction;
            access = AccessLevel.TextOnly;
            return true;
        }

        return false;
    }
}
