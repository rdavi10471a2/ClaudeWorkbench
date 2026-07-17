namespace ExternalCorpus.App;

internal sealed class AppCaller
{
    private readonly ExternalCorpus.Library.LibraryService service = new();

    public int Caller() => service./*<bind>*/Compute/*</bind>*/(21);
}
