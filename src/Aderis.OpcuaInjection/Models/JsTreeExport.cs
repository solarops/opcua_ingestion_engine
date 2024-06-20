namespace Aderis.OpcuaInjection.Models;

// Future: Consider a custom JSON encoder to write fields to needed structure, such as CheckCallback -> check_callback

public class JsTreeExport
{
    public JsCoreConfig Core { get; set; } = new();

    // types

    // add search automatically
    public List<string> Plugins { get; set; } = ["search"];

    public JsSearchSettings Search { get; set; } = new();
}

public class JsSearchSettings
{
    public bool Show_only_matches { get; set; } = true;
}

public class JsCoreConfig
{
    public int Animation { get; set; } = 0;
    public bool Check_callback { get; set; } = true;
    public JsCoreThemes Themes { get; set; } = new();
    public List<JsTreeNode> Data { get; set; } = new();
}

public class JsCoreThemes
{
    public bool Stripes { get; set; } = false;
    public bool Dots { get; set; } = false;
}

public class JsTreeNode
{
    public string Text { get; set; } = "";
    public string Id { get; set; } = "";

    // Re-enable for Type plugin support
    // public required string Type { get; set; }
    public JsTreeNodeData Data { get; set; } = new();
    public JsTreeNodeState State { get; set; } = new();
    public List<JsTreeNode> Children { get; set; } = [];
}

public class JsTreeNodeData
{
    public string Type { get; set; } = "Object";
}

public class JsTreeNodeState
{
    public bool Opened { get; set; } = true;
    public bool Selected { get; set; } = false;
}