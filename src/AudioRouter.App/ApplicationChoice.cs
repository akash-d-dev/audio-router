using System.ComponentModel;
using AudioRouter.Audio;

namespace AudioRouter.App;

public sealed class ApplicationChoice(ApplicationSource source, bool selected = false) : INotifyPropertyChanged
{
    private ApplicationSource source = source;
    private bool selected = selected;
    public ApplicationSource Source => source;
    public string Name => source.Name;
    public string ExecutablePath => source.ExecutablePath;
    public string Detail => source.Detail;
    public bool IsSelected
    {
        get => selected;
        set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void UpdateSource(ApplicationSource updated)
    {
        source = updated;
        PropertyChanged?.Invoke(this, new(nameof(Source)));
        PropertyChanged?.Invoke(this, new(nameof(Name)));
        PropertyChanged?.Invoke(this, new(nameof(Detail)));
    }
}
