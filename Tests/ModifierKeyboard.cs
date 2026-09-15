using System.Reflection;
using System.Windows.Input;

// Scoped to this test's WPF dispatcher. No OS keyboard state or other application's input is changed.
internal sealed class ModifierKeyboard : KeyboardDevice, IDisposable
{
    private readonly InputManager _manager;
    private readonly FieldInfo _field;
    private readonly object? _original;
    internal ModifierKeys Held { get; set; }
    internal ModifierKeyboard() : base(InputManager.Current)
    {
        _manager = InputManager.Current;
        _field = typeof(InputManager).GetField("_primaryKeyboardDevice", BindingFlags.Instance | BindingFlags.NonPublic)!;
        _original = _field.GetValue(_manager);
        _field.SetValue(_manager, this);
    }
    protected override KeyStates GetKeyStatesFromSystem(Key key) =>
        ((key is Key.LeftShift or Key.RightShift) && Held.HasFlag(ModifierKeys.Shift)) ||
        ((key is Key.LeftCtrl or Key.RightCtrl) && Held.HasFlag(ModifierKeys.Control)) ? KeyStates.Down : KeyStates.None;
    public void Dispose() { Held = ModifierKeys.None; _field.SetValue(_manager, _original); }
}
