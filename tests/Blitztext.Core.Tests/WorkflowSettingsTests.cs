using Blitztext.Core.Models;

namespace Blitztext.Core.Tests;

public sealed class WorkflowSettingsTests
{
    [Fact]
    public void CreateDefaults_ContainsEveryWorkflow()
    {
        var defaults = WorkflowSettings.CreateDefaults();

        foreach (var workflowType in Enum.GetValues<WorkflowType>())
        {
            Assert.True(defaults.ContainsKey(workflowType));
            Assert.True(defaults[workflowType].Enabled);
            Assert.True(defaults[workflowType].Hotkey.IsUsable());
        }
    }

    [Fact]
    public void HotkeyBinding_DisplayText_FormatsReadableShortcut()
    {
        var binding = new HotkeyBinding(Control: true, Alt: true, Shift: true, Key: "Space");

        Assert.Equal("Ctrl + Alt + Shift + Space", binding.DisplayText);
    }

    [Fact]
    public void HotkeyBinding_ModifierOnlyCombo_IsUsableAndModifierOnly()
    {
        var binding = new HotkeyBinding(Control: true, Windows: true);

        Assert.True(binding.IsModifierOnly);
        Assert.True(binding.IsUsable());
        Assert.Equal("Ctrl + Win", binding.DisplayText);
    }

    [Fact]
    public void HotkeyBinding_SingleModifierWithoutKey_IsNotUsable()
    {
        var binding = new HotkeyBinding(Control: true);

        Assert.True(binding.IsModifierOnly);
        Assert.False(binding.IsUsable());
    }

    [Fact]
    public void EnsureDefaults_KeepsModifierOnlyHotkey()
    {
        var app = new AppSettings();
        app.Workflows[WorkflowType.Transcription] = new WorkflowSettings
        {
            Enabled = true,
            Hotkey = new HotkeyBinding(Control: true, Windows: true)
        };

        WorkflowSettings.EnsureDefaults(app);

        Assert.Equal("Ctrl + Win", app.Workflows[WorkflowType.Transcription].Hotkey.DisplayText);
    }

    [Fact]
    public void EnsureDefaults_ResetsUnusableHotkeyToDefault()
    {
        var app = new AppSettings();
        app.Workflows[WorkflowType.TextImprover] = new WorkflowSettings
        {
            Enabled = true,
            Hotkey = new HotkeyBinding()
        };

        WorkflowSettings.EnsureDefaults(app);

        Assert.True(app.Workflows[WorkflowType.TextImprover].Hotkey.IsUsable());
    }
}
