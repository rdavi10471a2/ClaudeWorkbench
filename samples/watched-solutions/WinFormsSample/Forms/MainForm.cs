using System.Windows.Forms;
using WinFormsSample.Repositories;

namespace WinFormsSample.Forms;

public partial class MainForm : Form
{
    private readonly CustomerService service = new CustomerService();

    public MainForm()
    {
        InitializeComponent();
    }

    private async void OnLoadClicked(object? sender, System.EventArgs e)
    {
        await service.LoadAsync(1);
    }
}
