using System.Text.RegularExpressions;
namespace Scout.Core;

public static class DestinationTemplate {
    public static readonly IReadOnlySet<string> Tokens = new HashSet<string>(["series", "year", "provider", "id"], StringComparer.Ordinal);
    public static string Literal(string path) => path.Replace("{", "{{").Replace("}", "}}");
    // Older saved destinations are literal paths, including braces. New Scouts
    // inherit; explicit source/custom choices survive later global changes.
    public static string Mode(RunWorkspace run) => run.DestinationMode switch {
        "default" or "custom" or "source"=>run.DestinationMode,
        ""=>!string.IsNullOrWhiteSpace(run.DestinationRoot)?"custom":"default",
        _=>throw new ArgumentException("Unknown destination choice for this Scout.")
    };
    public static string ForRun(AppSettings settings, RunWorkspace run) => Mode(run) switch {
        "default"=>settings.DestinationTemplate,"source"=>"",
        _=>run.DestinationMode==""?Literal(run.DestinationRoot):!string.IsNullOrWhiteSpace(run.DestinationTemplateOverride)?run.DestinationTemplateOverride:
            throw new ArgumentException("Enter a destination template for this Scout, or choose Use global default or Keep in source folder.")
    };

    static (string Root, List<string> Folders) Parse(string template) {
        template=template.Trim();
        if(template.Length>2048||template.Any(char.IsControl))throw new ArgumentException("Destination template must be a single path of at most 2048 characters.");
        if(!Path.IsPathFullyQualified(template))throw new ArgumentException("Destination template must start with a full drive or network-share path, for example E:\\TV\\{series}.");
        var root=Path.GetPathRoot(template)!;
        if(!Regex.IsMatch(root,@"^[A-Za-z]:[\\/]$")&&!Regex.IsMatch(root,@"^\\\\(?![?.]\\)[^{}<>:""/\\|?*]+\\[^{}<>:""/\\|?*]+\\?$"))
            throw new ArgumentException("Use a literal drive or network share before destination tokens. Device paths are not supported.");
        if(root.StartsWith(@"\\")&&root.Split('\\',StringSplitOptions.RemoveEmptyEntries).Any(p=>p.Trim() is "." or ".."))
            throw new ArgumentException("Use a valid network server and share name.");
        var remainder=template[root.Length..];
        // Windows UNC roots omit the separator following the share name.
        if(!root.EndsWith('\\')&&!root.EndsWith('/')&&remainder.Length>0&&remainder[0] is '\\' or '/')remainder=remainder[1..];
        remainder=remainder.TrimEnd('\\','/');
        var folders=remainder.Length==0?new List<string>():NamingDsl.SplitPath(remainder);
        if(folders.Count>16||folders.Any(p=>string.IsNullOrWhiteSpace(p)||p.Trim() is "." or ".."))
            throw new ArgumentException("Destination folders cannot be empty or use . or .. components.");
        foreach(var folder in folders) {
            NamingDsl.ValidateTextComponent(folder,Tokens);
            if(folder.EndsWith(' ')||folder.EndsWith('.')||Regex.IsMatch(folder,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)",RegexOptions.IgnoreCase))
                throw new ArgumentException("Destination folders cannot use reserved Windows names or end with a space or period.");
        }
        return(root,folders);
    }
    public static void Validate(string template) {if(!string.IsNullOrWhiteSpace(template))Parse(template);}
    public static string? Resolve(string template,string series,SeriesIdentity? identity=null) {
        if(string.IsNullOrWhiteSpace(template))return null;
        var (root,folders)=Parse(template);
        string Token(string token) {
            if(token=="series")return !string.IsNullOrWhiteSpace(identity?.Name)?identity.Name:
                !string.IsNullOrWhiteSpace(series)?series:throw new ArgumentException("Identify the series before using {series} in the destination.");
            if(identity==null)throw new ArgumentException("Select a catalog show before using {"+token+"} in the destination.");
            identity.Validate();
            return token switch {
                "provider"=>identity.Provider,"id"=>identity.Id,
                "year"=>Regex.IsMatch(identity.Year,@"^\d{4}$")?identity.Year:throw new ArgumentException("The selected show has no release year. Choose a template without {year}, or select a catalog show with a year."),
                _=>throw new ArgumentException("Unknown destination token: "+token)
            };
        }
        var result=root;
        foreach(var folder in folders)result=Path.Combine(result,NamingDsl.RenderTextComponent(folder,Token));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(result));
    }
}
