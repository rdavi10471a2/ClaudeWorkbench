namespace ExternalCorpus;

internal interface IConstraintTarget
{
}

internal sealed class GenericConstraintCases<T>
    where T : /*<bind>*/IConstraintTarget/*</bind>*/
{
}
