namespace Valleysoft.Dredge;

internal struct ImageContentPaths
{
    private string? singlePath;
    private HashSet<string>? multiplePaths;

    public readonly int Count => multiplePaths?.Count ?? (singlePath is null ? 0 : 1);

    public void Add(string path, StringComparer comparer)
    {
        if (multiplePaths is not null)
        {
            multiplePaths.Add(path);
        }
        else if (singlePath is null)
        {
            singlePath = path;
        }
        else if (!comparer.Equals(singlePath, path))
        {
            multiplePaths = new(comparer) { singlePath, path };
            singlePath = null;
        }
    }

    public void Remove(string path, StringComparer comparer)
    {
        if (multiplePaths is not null)
        {
            if (multiplePaths.Remove(path) && multiplePaths.Count == 1)
            {
                foreach (string remaining in multiplePaths)
                {
                    singlePath = remaining;
                    break;
                }
                multiplePaths = null;
            }
        }
        else if (comparer.Equals(singlePath, path))
        {
            singlePath = null;
        }
    }

    public readonly Enumerator GetEnumerator() => new(singlePath, multiplePaths);

    public struct Enumerator : IDisposable
    {
        private string? singlePath;
        private HashSet<string>.Enumerator multiple;
        private readonly bool hasMultiple;

        internal Enumerator(string? singlePath, HashSet<string>? multiplePaths)
        {
            this.singlePath = singlePath;
            hasMultiple = multiplePaths is not null;
            multiple = multiplePaths is not null ? multiplePaths.GetEnumerator() : default;
            Current = "";
        }

        public string Current { get; private set; }

        public bool MoveNext()
        {
            if (singlePath is not null)
            {
                Current = singlePath;
                singlePath = null;
                return true;
            }
            if (hasMultiple && multiple.MoveNext())
            {
                Current = multiple.Current;
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (hasMultiple) multiple.Dispose();
        }
    }
}
