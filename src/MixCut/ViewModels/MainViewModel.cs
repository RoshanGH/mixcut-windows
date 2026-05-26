using CommunityToolkit.Mvvm.ComponentModel;

namespace MixCut.ViewModels;

/// <summary>主窗口 ViewModel：持有各子 ViewModel 与导航状态。对应 macOS 版 ContentView 的状态。</summary>
public partial class MainViewModel : ObservableObject
{
    public ProjectViewModel ProjectVM { get; }
    public ImportViewModel ImportVM { get; }
    public SegmentLibraryViewModel SegmentVM { get; }
    public SchemeViewModel SchemeVM { get; }

    [ObservableProperty]
    private NavigationItem _selectedNavItem = NavigationItem.Overview;

    public MainViewModel(
        ProjectViewModel projectVM,
        ImportViewModel importVM,
        SegmentLibraryViewModel segmentVM,
        SchemeViewModel schemeVM)
    {
        ProjectVM = projectVM;
        ImportVM = importVM;
        SegmentVM = segmentVM;
        SchemeVM = schemeVM;
    }

    public IReadOnlyList<NavigationItem> NavItems => NavigationItemExtensions.All;
}
