namespace BlazorSample.Components;

public partial class CustomerList
{
    public int LoadedCount { get; private set; }

    private void RecordLoad() => LoadedCount++;
}
