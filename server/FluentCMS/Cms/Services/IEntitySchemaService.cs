using System.Collections.Immutable;
using FluentCMS.Cms.Models;
using FluentCMS.Utils.QueryBuilder;
using FluentResults;

namespace FluentCMS.Cms.Services;

public interface IEntitySchemaService: IEntityVectorResolver, IAttributeValueResolver
{
    Task<Result<LoadedEntity>> LoadEntity(string name, CancellationToken token = default);
    Task<Entity?> GetTableDefine(string table, CancellationToken token);
    Task<Schema> SaveTableDefine(Schema schema, CancellationToken ct);
    Task<Schema> AddOrUpdateByName(Entity entity, CancellationToken ct =default);

    Task<Result<LoadedAttribute>> LoadSingleAttrByName(LoadedEntity entity, string attrName,bool loadInListLookup, CancellationToken ct);
    ValueTask<ImmutableArray<Entity>> AllEntities(CancellationToken ct = default);
    Task Delete(Schema schema, CancellationToken ct);
    Task<Schema> Save(Schema schema, CancellationToken ct);
    Task SaveTableDefine(Entity entity, CancellationToken token = default);
}