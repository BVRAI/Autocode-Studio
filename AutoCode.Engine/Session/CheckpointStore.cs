namespace AutoCode.Engine.Session;

public sealed class CheckpointStore
{
    private readonly string _snapshotDir;
    private readonly string _trashDir;
    private int _turn;
    private int _step;
    private readonly Dictionary<string, string?> _stepSnapshots = new(StringComparer.OrdinalIgnoreCase);

    public CheckpointStore(string sessionDirectory)
    {
        _snapshotDir = Path.Combine(sessionDirectory, "snapshots");
        _trashDir = Path.Combine(sessionDirectory, "trash");
        Directory.CreateDirectory(_snapshotDir);
        Directory.CreateDirectory(_trashDir);
    }

    public void BeginTurn()
    {
        _turn += 1;
        _step = 0;
        _stepSnapshots.Clear();
    }

    public void BeginStep()
    {
        _step += 1;
        _stepSnapshots.Clear();
    }

    public void SnapshotBeforeWrite(string absolutePath)
    {
        absolutePath = Path.GetFullPath(absolutePath);
        if (_stepSnapshots.ContainsKey(absolutePath))
        {
            return;
        }

        var content = File.Exists(absolutePath) ? File.ReadAllText(absolutePath) : null;
        _stepSnapshots[absolutePath] = content;
        var safeName = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(absolutePath))
            .Replace('/', '_')
            .Replace('+', '-');
        var path = Path.Combine(_snapshotDir, $"{_turn:D4}-{_step:D4}-{safeName}.txt");
        File.WriteAllText(path, content ?? "__AUTOCODE_FILE_DID_NOT_EXIST__");
    }

    public TrashRecord Trash(string absolutePath)
    {
        absolutePath = Path.GetFullPath(absolutePath);
        if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
        {
            throw new FileNotFoundException("Path does not exist.", absolutePath);
        }

        var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..32];
        var trashTarget = Path.Combine(_trashDir, id);
        if (Directory.Exists(absolutePath))
        {
            Directory.Move(absolutePath, trashTarget);
        }
        else
        {
            Directory.CreateDirectory(_trashDir);
            File.Move(absolutePath, trashTarget);
        }

        var record = new TrashRecord(id, absolutePath, trashTarget, DateTimeOffset.Now);
        File.WriteAllText(Path.Combine(_trashDir, id + ".meta"), string.Join(Environment.NewLine, [
            record.Id,
            record.OriginalPath,
            record.TrashPath,
            record.DeletedAt.ToString("O")
        ]));
        return record;
    }

    public IReadOnlyList<TrashRecord> ListTrash()
    {
        if (!Directory.Exists(_trashDir))
        {
            return [];
        }

        var outList = new List<TrashRecord>();
        foreach (var meta in Directory.EnumerateFiles(_trashDir, "*.meta"))
        {
            try
            {
                var lines = File.ReadAllLines(meta);
                if (lines.Length >= 4)
                {
                    outList.Add(new TrashRecord(lines[0], lines[1], lines[2], DateTimeOffset.Parse(lines[3])));
                }
            }
            catch
            {
                // Ignore corrupt trash metadata.
            }
        }

        return outList.OrderByDescending(t => t.DeletedAt).ToList();
    }

    public TrashRecord? Restore(string id)
    {
        var record = ListTrash().FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (record is null || (!File.Exists(record.TrashPath) && !Directory.Exists(record.TrashPath)))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(record.OriginalPath)!);
        if (Directory.Exists(record.TrashPath))
        {
            Directory.Move(record.TrashPath, record.OriginalPath);
        }
        else
        {
            File.Move(record.TrashPath, record.OriginalPath);
        }

        var meta = Path.Combine(_trashDir, id + ".meta");
        if (File.Exists(meta))
        {
            File.Delete(meta);
        }

        return record;
    }
}

public sealed record TrashRecord(string Id, string OriginalPath, string TrashPath, DateTimeOffset DeletedAt);
