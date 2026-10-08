using System.Text;

namespace Factory.Harness.Registry;

internal sealed record DslElement(string Kind, string Name, string Path, Dictionary<string, string> Properties, string? Variable = null);

/// `a -> b`, by the variable names the model gave its elements.
internal sealed record DslRelationship(string Source, string Destination);

/// Read-only Structurizr DSL reader, limited to what System 2 needs: people, software systems,
/// containers and components with their properties, and the relationships between them. Views and styles are skipped.
/// Anything it cannot place safely (!include, block comments, unbalanced braces) is an error, not
/// a silent miss: a missing element would let a task write outside its code area.
internal sealed class DslModel
{
    private static readonly HashSet<string> ElementKeywords = ["person", "softwareSystem", "container", "component"];

    public List<DslElement> Elements { get; } = [];
    public List<DslRelationship> Relationships { get; } = [];

    public static DslModel? Parse(string text, List<string> errors)
    {
        var model = new DslModel();
        var before = errors.Count;
        var stack = new Stack<(string Frame, DslElement? Element)>();
        var lineNo = 0;

        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;
            if (line.StartsWith("/*")) { errors.Add($"model line {lineNo}: block comments are not supported"); continue; }
            if (line.StartsWith("!include")) { errors.Add($"model line {lineNo}: !include is not supported; freeze a single-file model"); continue; }
            if (line.StartsWith('!')) continue;

            var tokens = Tokenize(line, lineNo, errors);
            if (tokens.Count == 0) continue;
            if (tokens is ["}"])
            {
                if (stack.Count == 0) errors.Add($"model line {lineNo}: '}}' closes nothing");
                else stack.Pop();
                continue;
            }

            var opens = tokens[^1] == "{";
            var (frame, owner) = stack.Count > 0 ? stack.Peek() : ("root", null);

            if (frame == "properties")
            {
                if (tokens.Count == 2 && owner is not null) owner.Properties[tokens[0]] = tokens[1];
                else if (tokens.Count != 2) errors.Add($"model line {lineNo}: a property needs a name and a value");
                if (opens) stack.Push(("other", null));
                continue;
            }

            var keywordAt = tokens.Count > 2 && tokens[1] == "=" ? 2 : 0;
            var keyword = tokens[keywordAt];

            if (tokens.Contains("->"))
            {
                if (tokens is [var source, "->", var destination, ..]) model.Relationships.Add(new DslRelationship(source, destination));
                if (opens) stack.Push(("other", null));
            }
            else if (ElementKeywords.Contains(keyword))
            {
                if (tokens.Count <= keywordAt + 1 || tokens[keywordAt + 1] == "{") { errors.Add($"model line {lineNo}: {keyword} has no name"); continue; }
                var name = tokens[keywordAt + 1];
                var parent = stack.Select(f => f.Element).FirstOrDefault(e => e is not null);
                var path = keyword == "person" || parent is null ? name : parent.Path + "/" + name;
                var el = new DslElement(keyword, name, path, new Dictionary<string, string>(StringComparer.Ordinal), keywordAt == 2 ? tokens[0] : null);
                model.Elements.Add(el);
                if (opens) stack.Push(("element", el));
            }
            else if (keyword == "properties" && opens)
            {
                stack.Push(("properties", frame == "element" ? owner : null));
            }
            else if (opens)
            {
                stack.Push(("other", null));
            }
        }

        if (stack.Count > 0) errors.Add($"model: {stack.Count} block(s) are never closed");
        return errors.Count == before ? model : null;
    }

    private static List<string> Tokenize(string line, int lineNo, List<string> errors)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < line.Length && line[i] != '"')
                {
                    if (line[i] == '\\' && i + 1 < line.Length) i++;
                    sb.Append(line[i++]);
                }
                if (i >= line.Length) { errors.Add($"model line {lineNo}: unterminated string"); return []; }
                i++;
                tokens.Add(sb.ToString());
                continue;
            }
            var start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != '"') i++;
            tokens.Add(line[start..i]);
        }
        return tokens;
    }
}
