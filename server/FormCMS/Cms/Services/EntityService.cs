using System.Text.Json;
using FormCMS.Core.HookFactory;
using FormCMS.Core.Descriptors;
using FormCMS.Infrastructure.RelationDbDao;
using FormCMS.Utils.DisplayModels;
using FormCMS.Utils.jsonElementExt;
using FormCMS.Utils.DataModels;
using FormCMS.Utils.EnumExt;
using FormCMS.Utils.RecordExt;
using FormCMS.Utils.ResultExt;
using DataType = FormCMS.Core.Descriptors.DataType;
using Task = System.Threading.Tasks.Task;

namespace FormCMS.Cms.Services;

public sealed class EntityService(
    IServiceProvider provider,
    KateQueryExecutor queryExecutor,
    IEntitySchemaService entitySchemaSvc,
    HookRegistry hookRegistry
) : IEntityService
{
   

    public async Task<ListResponse?> ListWithAction(
        string name, 
        ListResponseMode mode, 
        Pagination pagination,
        StrArgs args, 
        CancellationToken ct)
    {
        var entity = (await entitySchemaSvc.LoadEntity(name, null, ct)).Ok();
        var (filters, sorts,validPagination) = await GetListArgs(entity, args,pagination);
        return await ListWithAction(entity,mode, filters, sorts, validPagination, ct);
    }

    public async Task<Record[]> ListAsTree(string name, CancellationToken ct)
    {
        var entity = await entitySchemaSvc.LoadEntity(name,null, ct).Ok();
        var parentField = entity.Attributes.FirstOrDefault(x =>
            x.DataType == DataType.Collection && x.GetCollectionTarget(out var entityName, out _) && entityName == name
        )?? throw new ResultException("Can not compose list result as tree, not find an collection attribute whose target is the entity.");
        
        parentField.GetCollectionTarget(out _, out var linkField);
        var attributes = entity.Attributes.Where(x=>x.Field ==entity.PrimaryKey || x.InList && x.IsLocal());
        var items = await queryExecutor.Many(entity.AllQueryForTree(attributes),ct);
        return items.ToTree(entity.PrimaryKey, linkField);
    }
    
    public async Task<Record> SingleByIdBasic(string entityName, string id, string[] attributes,
        CancellationToken ct)
    {
        var ctx = await GetIdCtx(entityName, id, ct);
        var fields = ctx.Entity.Attributes
            .Where(x => x.IsLocal() && attributes.Contains(x.Field))
            .Select(x=>x.AddTableModifier());
        var query = ctx.Entity.ByIdsQuery( fields,[ctx.Id],null);
        return await queryExecutor.Single(query, ct) ??
               throw new ResultException($"not find record by [{id}]");
    }

    public async Task<Record> SingleWithAction(string entityName, string id, CancellationToken ct = default)
    {
        var ctx = await GetIdCtx(entityName, id, ct);
        var res = await hookRegistry.EntityPreGetSingle.Trigger(provider,
            new EntityPreGetSingleArgs(ctx.Entity, id, null));
        if (res.OutRecord is not null)
        {
            return res.OutRecord;
        }

        var attr = ctx.Entity.Attributes
            .Where(x =>
                x.Field == ctx.Entity.PrimaryKey
                ||x.Field == DefaultAttributeNames.PublishedAt.Camelize()
                ||x.Field == DefaultAttributeNames.PublicationStatus.Camelize()
                ||x.Field == DefaultColumnNames.UpdatedAt.Camelize()
                || x.InDetail && x.IsLocal())
            .ToArray();
        
        var query = ctx.Entity.ByIdsQuery(
            attr.Select(x=>x.AddTableModifier()), [ctx.Id],null);
        var record = await queryExecutor.Single(query, ct) ??
                     throw new ResultException($"not find record by [{id}]");

        await LoadItems(attr, [record], ct);
        
        return record;
    }

    public async Task<Record> InsertWithAction(string name, JsonElement ele, CancellationToken ct)
    {
        return await InsertWithAction(await GetRecordCtx(name, ele, ct), ct);
    }


    public async Task<Record> UpdateWithAction(string name, JsonElement ele, CancellationToken ct)
    {
        return await UpdateWithAction(await GetRecordCtx(name, ele, ct), ct);
    }

    public async Task<Record> DeleteWithAction(string name, JsonElement ele, CancellationToken ct)
    {
        return await Delete(await GetRecordCtx(name, ele, ct), ct);
    }

    public async Task SavePublicationSettings(string name, JsonElement ele, CancellationToken ct)
    {
        var (entity, record) = await GetRecordCtx(name, ele, ct);
        if (!record.TryGetValue(entity.PrimaryKey, out  var id) && id is null)
        {
            throw new ResultException($"Failed to get Record Id, cannot find [{name}]");
        }
        var query = entity.SavePublicationStatus(id, ele.ToDictionary() ).Ok();
        await queryExecutor.ExecAndGetAffected(query, ct);
    }

    public async Task<LookupListResponse> LookupList(string name, string startsVal, CancellationToken ct = default)
    {
        var (entity, sorts, pagination, attributes) = await GetLookupContext(name, ct);
        var count = await queryExecutor.Count(entity.Basic(), ct);
        if (count < entity.DefaultPageSize)
        {
            //not enough for one page, search in a client
            var query = entity.ListQuery([], sorts, pagination, null, attributes,null);
            var items = await queryExecutor.Many(query, ct);
            return new LookupListResponse(false, items);
        }

        ValidFilter[] filters = [];
        if (!string.IsNullOrEmpty(startsVal))
        {
            var constraint = new Constraint(Matches.StartsWith, [startsVal]);
            var filter = new Filter(entity.LabelAttributeName, MatchTypes.MatchAll, [constraint]);
            filters = (await FilterHelper.ToValidFilters([filter], entity,null, entitySchemaSvc, entitySchemaSvc)).Ok();
        }

        var queryWithFilters = entity.ListQuery(filters, sorts, pagination, null, attributes,null);
        var filteredItems = await queryExecutor.Many(queryWithFilters, ct);
        return new LookupListResponse(true, filteredItems);
    }

    public async Task<int> JunctionDelete(string name, string id, string attr, JsonElement[] elements,
        CancellationToken ct)
    {
        var ctx = await GetJunctionCtx(name, id, attr, ct);
        var items = elements.Select(ele =>
            ctx.Junction.TargetEntity.Parse(ele, entitySchemaSvc).Ok()).ToArray();

        var res = await hookRegistry.JunctionPreDel.Trigger(provider,
            new JunctionPreDelArgs(ctx.Entity, id, ctx.Attribute, items));

        var query = ctx.Junction.Delete(ctx.Id, res.RefItems);
        var ret = await queryExecutor.ExecAndGetAffected(query, ct);
        return ret;
    }

    public async Task<int> JunctionSave(string name, string id, string attr, JsonElement[] elements,
        CancellationToken ct)
    {
        var ctx = await GetJunctionCtx(name, id, attr, ct);

        var items = elements
            .Select(ele => ctx.Junction.TargetEntity.Parse(ele, entitySchemaSvc).Ok()).ToArray();
        var res = await hookRegistry.JunctionPreAdd.Trigger(provider,
            new JunctionPreAddArgs(ctx.Entity, id, ctx.Attribute, items));
        var query = ctx.Junction.Insert(ctx.Id, res.RefItems);

        var ret = await queryExecutor.ExeAndGetId(query, ct);
        return ret;
    }

    public async Task<object[]> JunctionTargetIds(string name, string sid, string attr, CancellationToken ct)
    {
        var (_,_, junction, id) = await GetJunctionCtx(name, sid, attr, ct);
        var query = junction.GetTargetIds([id]);
        var records = await queryExecutor.Many(query, ct);
        return records.Select(x => x[junction.TargetAttribute.Field]).ToArray();
    }
    
    public async Task<ListResponse> JunctionList(string name, string sid, string attr, bool exclude,
        Pagination pagination,
        StrArgs args, CancellationToken ct)
    {
        var (_,_, junction, id) = await GetJunctionCtx(name, sid, attr, ct);
        var target = junction.TargetEntity;

        var attrs = target.Attributes
            .Where(x=>x.Field == target.PrimaryKey || x.IsLocal() && x.InList)
            .ToArray();

        var (filters, sorts, validPagination) = await GetListArgs(target, args, pagination);

        var listQuery = exclude
            ? junction.GetNotRelatedItems(attrs, filters, sorts, validPagination, [id])
            : junction.GetRelatedItems(filters, [..sorts], validPagination, null, attrs, [id],null);

        var countQuery = exclude
            ? junction.GetNotRelatedItemsCount(filters, [id])
            : junction.GetRelatedItemsCount(filters, [id]);

        var items = await queryExecutor.Many(listQuery, ct);
        await LoadItems( attrs, items, ct);
        return new ListResponse(items, await queryExecutor.Count(countQuery, ct));
    }

    public async Task<Record> CollectionInsert(string name, string sid, string attr, JsonElement element, CancellationToken ct = default)
    {
        var (collection,id) = await GetCollectionCtx(name, sid, attr, ct);
        var item = collection.TargetEntity.Parse(element, entitySchemaSvc).Ok();
        item[collection.LinkAttribute.Field] = id.ObjectValue!;
        return await InsertWithAction(new RecordContext(collection.TargetEntity, item), ct);
    }

    public async Task<ListResponse> CollectionList(string name, string sid, string attr, Pagination pagination, StrArgs args, CancellationToken ct = default)
    {
        var (collection,id) = await GetCollectionCtx(name, sid, attr, ct);
        var (filters, sorts,validPagination) = await GetListArgs(collection.TargetEntity, args,pagination);

        var attributes = collection.TargetEntity.Attributes
            . Where(x=> x.Field == collection.TargetEntity.PrimaryKey || x.IsLocal() && x.InList)
            .ToArray();    
        
        var listQuery = collection.List(filters,sorts,validPagination,null,attributes,[id],null);
        var items = await queryExecutor.Many(listQuery, ct);
        await LoadItems( attributes, items, ct);
      
        var countQuery = collection.Count(filters,[id]);
        return new ListResponse( items, await queryExecutor.Count(countQuery, ct));
    }

    
    private async Task<ListResponse?> ListWithAction(
        
        LoadedEntity entity, 
        ListResponseMode mode,
        ValidFilter[] filters,
        ValidSort[] sorts, 
        ValidPagination pagination, 
        CancellationToken ct)
    {
        var args = new EntityPreGetListArgs(
            Entity: entity,
            RefFilters: [..filters],
            RefSorts: [..sorts],
            RefPagination: pagination
        );

        var res = await hookRegistry.EntityPreGetList.Trigger(provider, args);
        var attributes = entity.Attributes
            .Where(x=>x.Field ==entity.PrimaryKey || x.InList && x.IsLocal())
            .ToArray();

        var countQuery = entity.CountQuery([..res.RefFilters],null);
        return mode switch
        {
            ListResponseMode.Count => new ListResponse([], await queryExecutor.Count(countQuery, ct)),
            ListResponseMode.Items => new ListResponse(await RetrieveItems(), 0),
            _ => new ListResponse(await RetrieveItems(), await queryExecutor.Count(countQuery, ct))
        };


        async Task<Record[]> RetrieveItems()
        {
            var listQuery = entity.ListQuery([..res.RefFilters], [..res.RefSorts], res.RefPagination, null, attributes,null);
            var items =  await queryExecutor.Many(listQuery, ct);
            await LoadItems(attributes, items, ct);
            return items;
        }
    }

    private async Task LoadItems(IEnumerable<LoadedAttribute> attr, Record[] items, CancellationToken ct)
    {
        if (items.Length == 0) return ;
        foreach (var attribute in attr)
        {
            if (attribute.DataType == DataType.Lookup)
            {
                await LoadLookupData(attribute, items, ct);
            }else if (attribute.IsCsv())
            {
                attribute.SpreadCsv(items);
            }
        }
    } 

    private async Task LoadLookupData(LoadedAttribute attr, Record[] items, CancellationToken token)
    {
        var ids = attr.GetUniq(items);
        if (ids.Length == 0) return;

        var lookup = attr.Lookup ??
              throw new ResultException($"not find lookup entity from {attr.AddTableModifier()}");

        var query = lookup.LookupTitleQuery(ids);
        
        var targetRecords = await queryExecutor.Many(query, token);
        foreach (var lookupRecord in targetRecords)
        {
            var lookupId = lookupRecord[lookup.TargetEntity.PrimaryKey];
            foreach (var item in items.Where(local =>
                         local[attr.Field] is not null && local[attr.Field].Equals(lookupId)))
            {
                item[attr.Field] = lookupRecord;
            }
        }
    }

    private async Task<Record> UpdateWithAction(RecordContext ctx, CancellationToken token)
    {
        var (entity, record) = ctx;

        ResultExt.Ensure(entity.ValidateLocalAttributes(record));
        ResultExt.Ensure(entity.ValidateTitleAttributes(record));

        var res = await hookRegistry.EntityPreUpdate.Trigger(provider,
            new EntityPreUpdateArgs(entity,record));

        record = res.RefRecord;
        var query = entity.UpdateQuery(record).Ok();
        
        var affected = await queryExecutor.ExecAndGetAffected(query, token);
        if (affected == 0)
        {
            throw new ResultException("Error: Concurrent Update Detected. Someone else has modified this item since you last accessed it. Please refresh the data and try again.");
        }
        await hookRegistry.EntityPostUpdate.Trigger(provider, new EntityPostUpdateArgs(entity,record));
        return record;
    }

    private async Task<Record> InsertWithAction(RecordContext ctx, CancellationToken token)
    {
        var (entity, record) = ctx;
        ResultExt.Ensure(entity.ValidateLocalAttributes(record));
        ResultExt.Ensure(entity.ValidateTitleAttributes(record));

        var res = await hookRegistry.EntityPreAdd.Trigger(provider,
            new EntityPreAddArgs(entity, record));
        record = res.RefRecord;
        
        var query = entity.Insert(record);
        var id = await queryExecutor.ExeAndGetId(query, token);
        record[entity.PrimaryKey] = id;

        await hookRegistry.EntityPostAdd.Trigger(provider,
            new EntityPostAddArgs(entity,record));
        return record;
    }

    private async Task<Record> Delete(RecordContext ctx, CancellationToken token)
    {
        var (entity, record) = ctx;

        var res = await hookRegistry.EntityPreDel.Trigger(provider,
            new EntityPreDelArgs(entity, record));
        record = res.RefRecord;


        var query = entity.DeleteQuery(record).Ok();
        var affected = await queryExecutor.ExecAndGetAffected(query, token);
        if (affected == 0)
        {
            throw new ResultException("Error: Concurrent Write Detected. Someone else has modified this item since you last accessed it. Please refresh the data and try again.");
        } 
        
        await hookRegistry.EntityPostDel.Trigger(provider, new EntityPostDelArgs(entity ,record));
        return record;
    }

    record IdContext(LoadedEntity Entity, ValidValue Id);

    private async Task<IdContext> GetIdCtx(string entityName, string id, CancellationToken token)
    {
        var entity = (await entitySchemaSvc.LoadEntity(entityName, null, token)).Ok();
        if (!entitySchemaSvc.ResolveVal(entity.PrimaryKeyAttribute, id, out var idValue))
        {
            throw new ResultException($"Failed to cast {id} to {entity.PrimaryKeyAttribute.DataType}");
        }

        return new IdContext(entity, idValue!.Value);
    }
    
    private record CollectionContext(Collection Collection, ValidValue Id );

    private async Task<CollectionContext> GetCollectionCtx(string entity, string sid, string attr, CancellationToken ct)
    {
        var loadedEntity = (await entitySchemaSvc.LoadEntity(entity, null, ct)).Ok();
        var collection = loadedEntity.Attributes.FirstOrDefault(x=>x.Field ==attr)?.Collection ??
                        throw new ResultException($"Failed to get Collection Context, cannot find [{attr}] in [{entity}]");

        if (!entitySchemaSvc.ResolveVal(loadedEntity.PrimaryKeyAttribute, sid, out var id))
        {
            throw new ResultException($"Failed to cast {sid} to {loadedEntity.PrimaryKeyAttribute.DataType}");
        }
        
        return new CollectionContext( collection, id!.Value);
    }


    private record JunctionContext(LoadedEntity Entity,LoadedAttribute Attribute, Junction Junction, ValidValue Id);
    

    private async Task<JunctionContext> GetJunctionCtx(string entity, string sid, string attr, CancellationToken ct)
    {
        var loadedEntity = (await entitySchemaSvc.LoadEntity(entity, null, ct)).Ok();
        var errMessage = $"Failed to Get Junction Context, cannot find [{attr}] in [{entity}]";
        var attribute = loadedEntity.Attributes.FirstOrDefault(x=>x.Field == attr) ??
                        throw new ResultException(errMessage);

        var junction = attribute.Junction ?? throw new ResultException(errMessage);
        if (!entitySchemaSvc.ResolveVal(junction.SourceAttribute, sid, out var id))
        {
            throw new ResultException($"Failed to cast {sid} to {junction.SourceAttribute.DataType}");
        }

        return new JunctionContext(loadedEntity,attribute, junction, id!.Value);
    }

    private record RecordContext(LoadedEntity Entity, Record Record);
    private async Task<RecordContext> GetRecordCtx(string name, JsonElement ele, CancellationToken token)
    {
        var entity = (await entitySchemaSvc.LoadEntity(name, null, token)).Ok();
        var record = entity.Parse(ele, entitySchemaSvc).Ok();
        return new RecordContext(entity, record);
    }

    private record LookupContext(LoadedEntity Entity, ValidSort[] Sorts, ValidPagination Pagination, LoadedAttribute[] Attributes);

    private async Task<LookupContext> GetLookupContext(string name, CancellationToken ct = default)
    {
        var entity = (await entitySchemaSvc.LoadEntity(name, null, ct)).Ok();
        var sort = new Sort(entity.LabelAttributeName, SortOrder.Asc);
        var validSort = (await SortHelper.ToValidSorts([sort], entity, entitySchemaSvc,null)).Ok();
        var pagination = PaginationHelper.ToValid(new Pagination(), entity.DefaultPageSize);
        return new LookupContext(entity, validSort, pagination,[entity.PrimaryKeyAttribute,entity.LabelAttribute]);
    }

    private record ListArgs(ValidFilter[] Filters, ValidSort[] Sorts, ValidPagination Pagination);
    private async Task<ListArgs> GetListArgs(LoadedEntity entity,  StrArgs args,Pagination pagination)
    {
        var (filters,sorts) = QueryStringParser.Parse(args);
        var validFilters = await filters.ToValidFilters(entity,null, entitySchemaSvc,entitySchemaSvc).Ok(); 
        var validSort = await sorts.ToValidSorts( entity, entitySchemaSvc,null).Ok();
        
        var validPagination = PaginationHelper.ToValid(pagination, entity.DefaultPageSize);
        return new ListArgs(validFilters, validSort, validPagination);
    }
}