using System.Windows;
using Microsoft.Win32;
using QvmManager.Models;

namespace QvmManager.Views;

public partial class NewVmWindow : Window
{
    public string VmName { get; private set; } = "New VM";
    public GuestOsType OsType { get; private set; } = GuestOsType.Linux;
    public string? IsoPath { get; private set; }

    public NewVmWindow()
    {
        InitializeComponent();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose an installer ISO",
            Filter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            IsoPathBox.Text = dlg.FileName;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Give the VM a name first.", "QVM Manager",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        VmName = name;
        OsType = OsTypeBox.SelectedIndex switch
        {
            0 => GuestOsType.Linux,
            1 => GuestOsType.WindowsGuest,
            _ => GuestOsType.Other
        };
        IsoPath = string.IsNullOrWhiteSpace(IsoPathBox.Text) ? null : IsoPathBox.Text;

        DialogResult = true;
    }
}
