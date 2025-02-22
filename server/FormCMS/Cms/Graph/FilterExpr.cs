using FormCMS.Core.Descriptors;
using GraphQL.Types;

namespace FormCMS.Cms.Graph;

public sealed class FilterExpr : InputObjectGraphType
{
    public FilterExpr()
    {
        Name = FilterConstants.FilterExprKey;
        AddField(new FieldType
        {
            Name = FilterConstants.FieldKey,
            Type = typeof(StringGraphType),
        });
        AddField(new FieldType
        {
            Name = FilterConstants.ClauseKey,
            Type = typeof(ListGraphType<Clause>)
        });
    }
}