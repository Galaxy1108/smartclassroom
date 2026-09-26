using Avalonia.Headless.XUnit;
using SmartClassroom.App.ViewModels;
using Xunit;

namespace SmartClassroom.App.Tests;

public sealed class SettingsTeacherTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sc-vm-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [AvaloniaFact]
    public void AddRemoveTeacher_Persists()
    {
        TestSetup.EnsureApp();
        var vm = new SettingsViewModel(_path)
        {
            NewTeacherQq = "10001",
            NewTeacherName = "张老师",
            NewTeacherSubject = "数学"
        };
        vm.AddTeacher();
        Assert.Single(vm.Teachers);

        var vm2 = new SettingsViewModel(_path);
        Assert.Single(vm2.Teachers);
        Assert.Equal("张老师", vm2.Teachers[0].Name);

        vm2.RemoveTeacher(vm2.Teachers[0]);
        Assert.Empty(vm2.Teachers);
        Assert.Empty(new SettingsViewModel(_path).Teachers);
    }

    [AvaloniaFact]
    public void AddTeacher_RequiresNameAndSubject()
    {
        TestSetup.EnsureApp();
        var vm = new SettingsViewModel(_path) { NewTeacherName = "", NewTeacherSubject = "" };
        vm.AddTeacher();
        Assert.Empty(vm.Teachers);
    }
}
