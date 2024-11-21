namespace FluentCMS.Utils.QueryBuilder;

public interface INameProvider
{
    string Name();
}

public interface IPairProvider:INameProvider
{
    bool Pairs(out (string, object)[] pairs);
}

public interface IValueProvider:INameProvider
{
    bool Vals(out string[] values);
    bool Val(out object? value);
}

public interface IObjectProvider:INameProvider
{
    bool Objects(out Record[] objects);
}