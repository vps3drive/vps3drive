namespace VPS3Drive.Core;

public sealed class ObjectPathMapper
{
    private readonly string _rootPrefix;

    public ObjectPathMapper(string rootPrefix = "")
    {
        _rootPrefix = rootPrefix.Trim('/');
    }

    public string ToObjectKey(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (relativePath.IndexOf('\0') >= 0)
            throw new ArgumentException("Paths cannot contain NUL characters.", nameof(relativePath));

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new ArgumentException("Relative traversal segments are not valid object paths.", nameof(relativePath));

        var joined = string.Join('/', segments);
        if (string.IsNullOrEmpty(_rootPrefix))
            return joined;
        return joined.Length == 0 ? _rootPrefix + "/" : _rootPrefix + "/" + joined;
    }

    public string ToRelativePath(string objectKey)
    {
        ArgumentNullException.ThrowIfNull(objectKey);
        var normalized = objectKey.Replace('\\', '/');
        if (!string.IsNullOrEmpty(_rootPrefix))
        {
            var prefix = _rootPrefix + "/";
            if (normalized == _rootPrefix || normalized == prefix.TrimEnd('/'))
                return string.Empty;
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
                throw new ArgumentException("Object key is outside the configured root prefix.", nameof(objectKey));
            normalized = normalized[prefix.Length..];
        }
        return normalized.TrimEnd('/');
    }
}
