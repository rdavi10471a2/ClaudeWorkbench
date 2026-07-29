using BlazorSample.Components;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Blazor Web App (interactive server). The existing Components/Model/Repositories render on the pages below.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

WebApplication app = builder.Build();

app.UseAntiforgery();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
