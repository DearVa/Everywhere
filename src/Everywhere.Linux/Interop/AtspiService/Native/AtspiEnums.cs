namespace Everywhere.Linux.Interop.AtspiBackend.Native;

public enum AtspiCoordType
{
    Screen = 0,
    Window = 1,
    Parent = 2
}

public enum AtspiRole
{
    Invalid = 0,
    
    // Text/Label elements
    Label = 29,
    Text = 61,
    Header = 71,
    Footer = 72,
    Caption = 81,
    Comment = 97,
    DescriptionTerm = 122,
    Footnote = 124,
    Paragraph = 73,
    DescriptionValue = 123,
    
    // Button elements
    Button = 43,
    ToggleButton = 62,
    PushButton = 129,
    
    // Text input elements
    Entry = 79,
    Editbar = 77,
    PasswordText = 40,
    
    // Document elements
    Article = 109,
    DocumentFrame = 82,
    DocumentSpreadsheet = 92,
    DocumentPresentation = 93,
    DocumentText = 94,
    DocumentWeb = 95,
    DocumentEmail = 96,
    HtmlContainer = 25,
    Form = 87,
    
    // Navigation elements
    Link = 88,
    
    // Image elements
    Image = 27,
    DesktopIcon = 13,
    Icon = 26,
    
    // Selection elements
    CheckBox = 7,
    Switch = 130,
    RadioButton = 44,
    ComboBox = 11,
    
    // List elements
    List = 31,
    ListBox = 98,
    DescriptionList = 121,
    ListItem = 32,
    
    // Tree elements
    TreeTable = 66,
    Tree = 65,
    
    // Tab elements
    PageTabList = 38,
    PageTab = 37,
    
    // Table elements
    Table = 55,
    TableRow = 90,
    
    // Menu elements
    Menu = 33,
    LandMark = 110,
    MenuItem = 35,
    CheckMenuItem = 8,
    TearoffMenuItem = 59,
    
    // Range controls
    Slider = 51,
    ScrollBar = 48,
    ProgressBar = 42,
    SpinButton = 52,
    
    // Container elements
    StatusBar = 54,
    ToolBar = 63,
    SplitPane = 53,
    ScrollPane = 49,
    RootPane = 46,
    Panel = 39,
    Canvas = 6,
    Section = 85,
    
    // Top-level elements
    Frame = 23,
    Window = 69,
    Application = 75,
}
public enum AtspiState
{
    Showing = 25,
    Visible = 30,
    Enabled = 8,
    Focused = 12,
    Selected = 23,
    Editable = 7
}

public enum AtspiRelationType
{
    SubwindowOf = 12,
    Embeds = 13,
    EmbeddedBy = 14
}

public enum AtspiLayer
{
    Invalid = 0,
    Background = 1,
    Canvas = 2,
    Widget = 3,
    Mdi = 4,
    Popup = 5,
    Overlay = 6,
    Window = 7
}