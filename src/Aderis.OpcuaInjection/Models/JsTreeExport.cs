namespace Aderis.OpcuaInjection.Models;

public class JsTreeExport
{
    public JsCoreConfig Core { get; set; } = new();

    // types

    public List<string> Plugins { get; set; } = new();
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
    public JsTreeNodeState State { get; set; } = new();
    public List<JsTreeNode> Children { get; set; } = [];
}

public class JsTreeNodeState
{
    public bool Opened { get; set; } = true;
    public bool Selected { get; set; } = false;
}