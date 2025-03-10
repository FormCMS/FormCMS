using FluentResults;
using FormCMS.Core.Descriptors;

namespace FormCMS.Cms.Services;
public static class SchemaName
{
    public const string TopMenuBar = "top-menu-bar";
}

public interface ISchemaService
{
    Task<Schema[]> All(SchemaType? type, IEnumerable<string>? names, PublicationStatus? publicationStatus, CancellationToken ct = default);
    Task<Schema[]> AllWithAction(SchemaType? type, PublicationStatus? publicationStatus, CancellationToken ct = default);

    Task<Schema?> ByIdWithAction(long id, CancellationToken ct = default);
    Task<Schema?> ById(long id, CancellationToken ct = default);

    Task<Schema[]> History(string schemaId, CancellationToken ct = default);
    
    
    Task<Result> NameNotTakenByOther(Schema schema, CancellationToken ct);
    Task<Schema?> GetByNameDefault(string name, SchemaType type,PublicationStatus?status, CancellationToken ct = default);
    Task<Schema?> GetByNamePrefixDefault(string name, SchemaType type,PublicationStatus?status, CancellationToken ct = default);

    Task Publish(Schema schema,CancellationToken ct  = default);
    
    Task<Schema> SaveWithAction(Schema schema, CancellationToken ct=default);
    Task<Schema> Save(Schema schema,CancellationToken ct  = default);
    
    Task<Schema> AddOrUpdateByNameWithAction(Schema schema, CancellationToken ct = default);
    Task Delete(long id, CancellationToken ct = default);
    
    Task EnsureTopMenuBar(CancellationToken ct = default);
    Task EnsureSchemaTable();

    Task RemoveEntityInTopMenuBar(Entity entity, CancellationToken ct = default);
    public Task EnsureEntityInTopMenuBar(Entity entity, CancellationToken ct = default);
}