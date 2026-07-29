using BlazorSample.Model;
using Microsoft.AspNetCore.Components;

namespace BlazorSample.Components;

// Code-behind for CustomerCard. Proper Razor discipline: the markup lives in CustomerCard.razor, the
// behaviour here in CustomerCard.razor.cs (this partial), and the scoped styles in CustomerCard.razor.css.
// A build (and the index over it) has to handle all three parts as one component.
public partial class CustomerCard
{
    [Parameter]
    public Customer Customer { get; set; } = new();
}
