using Employee.Infrastructure;
using Employee.Core;

internal static class ProjectScanChecks
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "EmployeeProjectScan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new SqliteStore(Path.Combine(root, "scan.db"));
            var parent = Path.Combine(root, "projects");
            Directory.CreateDirectory(parent);
            void Marker(string relative, string marker)
            {
                var folder = Path.Combine(parent, relative);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, marker), "");
            }

            Marker("web", "package.json");
            Marker("web/nested", "pyproject.toml");
            Marker("group/service", "go.mod");
            Marker("solution", "Demo.slnx");
            Marker("solution/src", "Demo.csproj");
            Marker("node_modules/ignored", "package.json");
            Marker(".hidden/ignored", "Cargo.toml");
            Marker("empty/obj/ignored", "package.json");
            Marker("existing", "requirements.txt");
            Marker("worktree", ".git");
            var scanner = new ProjectIndexer(store);
            scanner.Register(Path.Combine(parent, "existing"), "Already registered", "");
            var result = scanner.ScanAndRegister(parent);
            Require(result.Added == 4 && result.Existing == 1 && result.Unreadable == 0 && !result.LimitReached, "Unexpected scan counts");
            Require(store.Projects().Count == 5, "Nested projects or dependencies were imported");
            Require(store.Projects().Any(p => p.Name == "service"), "Nested grouping folder was not scanned");
            result = scanner.ScanAndRegister(parent);
            Require(result.Added == 0 && result.Existing == 5, "Repeated scan created duplicates");
            result = scanner.ScanAndRegister(Path.Combine(parent, "web"));
            Require(result.Added == 0 && result.Existing == 1, "Selected project root should be recognized");
            var web = store.Projects().Single(p => p.Name == "web");
            File.WriteAllText(Path.Combine(web.Root, "README.md"), "Quan ly thu vien: borrowing catalog librarian");
            Directory.CreateDirectory(Path.Combine(web.Root, "node_modules"));
            File.WriteAllText(Path.Combine(web.Root, "node_modules", "SecretNebula.cs"), "");
            File.WriteAllText(Path.Combine(web.Root, ".env"), "PASSWORD=HiddenCredential");
            web = scanner.RefreshKeywords(web);
            Require(web.Keywords.Contains("borrowing") && !web.Keywords.Contains("nebula") && !web.Keywords.Contains("credential"), "Keyword scan included excluded content or missed README");
            Require(scanner.Resolve("Improve borrowing catalog")?.Id == web.Id, "Keyword assignment failed");
            Require(scanner.Resolve("Quan ly thư viện")?.Id == web.Id, "Accent-insensitive assignment failed");
            Require(scanner.Resolve("Improve borrowing") == null, "One keyword must require selection");
            var solution = store.Projects().Single(p => p.Name == "solution");
            store.SaveProject(solution with { Keywords = "borrowing catalog librarian" });
            Require(scanner.Resolve("Improve borrowing catalog") == null, "Shared keywords must require selection");
            Require(scanner.Resolve("solution borrowing catalog")?.Id == solution.Id, "Explicit name should take precedence");
            Require(store.Projects().Single(p => p.Id == web.Id).KeywordsUpdatedAt != null, "Keywords were not persisted");
            using (var reopened = new SqliteStore(Path.Combine(root, "scan.db")))
                Require(new ProjectIndexer(reopened).Resolve("Quan ly thư viện")?.Id == web.Id, "Keyword discovery did not survive reopening the database");
            File.WriteAllText(Path.Combine(web.Root, "README.md"), "astronomy telescope observatory");
            scanner.RefreshKeywords(web);
            Require(!store.Projects().Single(p => p.Id == web.Id).Keywords.Contains("borrowing"), "Rescan retained stale keywords");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                scanner.ScanAndRegister(parent, cancellation.Token);
                throw new Exception("Cancellation ignored");
            }
            catch (OperationCanceledException)
            {
            }

            var removed = store.Projects().First(p => p.Name == "service");
            var retained = store.Projects().First(p => p.Id != removed.Id);
            var work = new WorkItem
            {
                ProjectId = removed.Id,
                State = WorkState.Running
            };
            store.SaveWork(work);
            var otherWork = new WorkItem
            {
                ProjectId = retained.Id
            };
            store.SaveWork(otherWork);
            store.Log(work.Id, "Test", "Remove me");
            store.Log(otherWork.Id, "Test", "Keep me");
            store.ReplaceIndex(removed.Id, [new(removed.Id, "go.mod", 0, 0)]);
            store.ReplaceIndex(retained.Id, [new(retained.Id, "keep.cs", 0, 0)]);
            var session = new WorkSession
            {
                ProjectIds = [removed.Id]
            };
            store.SaveSession(session);
            store.SavePlan(new() { SessionId = session.Id });
            store.SaveDecision(new() { SessionId = session.Id });
            store.SaveTestProfile(new() { ProjectId = removed.Id });
            store.SaveTestJob(new() { ProjectId = removed.Id });
            try
            {
                store.DeleteProject(removed.Id);
                throw new Exception("Running project was deleted");
            }
            catch (InvalidOperationException)
            {
            }

            Require(store.Projects().Count == 5, "Blocked deletion changed data");
            work.State = WorkState.Paused;
            store.SaveWork(work);
            session.ProjectIds.Add(retained.Id);
            store.SaveSession(session);
            try
            {
                store.DeleteProject(removed.Id);
                throw new Exception("Shared session was deleted");
            }
            catch (InvalidOperationException)
            {
            }

            session.ProjectIds.Remove(retained.Id);
            store.SaveSession(session);
            store.DeleteProject(removed.Id);
            Require(store.Projects().Count == 4 && !store.Projects().Any(p => p.Id == removed.Id), "Project was not deleted");
            Require(store.WorkItems().Single().Id == otherWork.Id && store.Logs(work.Id).Count == 0 && store.Logs(otherWork.Id).Count == 1, "Work/history deletion was not scoped");
            Require(store.Sessions().Count == 0 && store.Plans(session.Id).Count == 0 && store.Decisions(session.Id).Count == 0, "Session data remained");
            Require(store.TestProfiles().Count == 0 && store.TestJobs().Count == 0, "Test data remained");
            Require(store.SearchFiles(removed.Id, "").Count == 0 && store.SearchFiles(removed.Id, "go").Count == 0 && store.SearchFiles(retained.Id, "keep").Count == 1, "Index deletion was not scoped");
            Require(File.Exists(Path.Combine(removed.Root, "go.mod")), "Project source was deleted");
            Require(scanner.Register(removed.Root, removed.Name, "").Id != removed.Id, "Deleted project could not be registered again");
            Console.WriteLine("PASS project deletion: running/shared-session guards, related data cleanup, other project isolation, source preservation, re-registration");
            Console.WriteLine("PASS project scan: recursive discovery, project boundaries, exclusions, duplicates, cancellation");
            return 0;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var cleanup = Path.GetFullPath(root);
            var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EmployeeProjectScan")) + Path.DirectorySeparatorChar;
            if (!cleanup.StartsWith(testRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid test cleanup path");
            Directory.Delete(cleanup, true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
