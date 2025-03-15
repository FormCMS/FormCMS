using FormCMS.Cms.Graph;
using FormCMS.Core.Assets;
using FormCMS.Core.Descriptors;
using FormCMS.Core.HookFactory;
using FormCMS.Infrastructure.RelationDbDao;
using FormCMS.Utils.RecordExt;
using FormCMS.Utils.ResultExt;
using FormCMS.Utils.StrArgsExt;
using Humanizer;

namespace FormCMS.Cms.Services;

public sealed class QueryService(
    ILogger<QueryService> logger,
    KateQueryExecutor executor,
    IQuerySchemaService schemaSvc,
    IEntitySchemaService resolver,
    IServiceProvider provider,
    HookRegistry hook
) : IQueryService
{
    public async Task<Record[]> ListWithAction(GraphQlRequestDto dto)
        => await ListWithAction(await FromGraphQlRequest(dto, dto.Args), new Span(),  dto.Args);

    public async Task<Record[]> ListWithAction(string name, Span span, Pagination pagination, StrArgs args, CancellationToken token)
        => await ListWithAction(await FromSavedQuery(name,pagination, !span.IsEmpty(),args,token), span, args, token);

    public async Task<Record?> SingleWithAction(GraphQlRequestDto dto)
        => await SingleWithAction(await FromGraphQlRequest(dto,dto.Args), dto.Args);

    public async Task<Record?> SingleWithAction(string name, StrArgs args, CancellationToken ct)
        => await SingleWithAction(await FromSavedQuery(name, null, false,args, ct),args,ct);

    public async Task<Record[]> Partial(string name, string attr, Span span, int limit, StrArgs args,
        CancellationToken token)
    {
        if (span.IsEmpty()) throw new ResultException("cursor is empty, can not partially execute query");

        var query = await schemaSvc.ByNameAndCache(name, PublicationStatusHelper.GetSchemaStatus(args), token);
        var attribute = query.Selection.RecursiveFind(attr)?? throw new ResultException("can not find attribute");
        var desc = attribute.GetEntityLinkDesc().Ok();
        
        var flyPagination = new Pagination(null, limit.ToString());
        var pagination = PaginationHelper.ToValid(flyPagination, attribute.Pagination,
            desc.TargetEntity.DefaultPageSize, true, args);

        var fields = attribute.Selection.Where(x => x.IsLocal()).ToArray();
        var validSpan = span.ToValid(fields, resolver).Ok();

        var filters = FilterHelper.ReplaceVariables(attribute.Filters,args, resolver).Ok();
        var sorts = (await SortHelper.ReplaceVariables(attribute.Sorts, args, desc.TargetEntity, resolver, PublicationStatusHelper.GetSchemaStatus(args))).Ok();

        var kateQuery = desc.GetQuery(fields, [validSpan.SourceId()],
            new CollectiveQueryArgs(filters, sorts, pagination.PlusLimitOne(), validSpan), PublicationStatusHelper.GetDataStatus(args));
        var records = await executor.Many(kateQuery, token);

        records = span.ToPage(records, pagination.Limit);
        
        if (records.Length <= 0) return records;

        await LoadItems(attribute.Selection, args, records, token);
        var sourceId = desc.TargetAttribute.GetValueOrLookup(records[0]);
        new[] { attribute }.SetSpan(records, attribute.Sorts, sourceId);
        await LoadAsset([..attribute.Selection],records);
        return records;
    }

    /*
     * why distinct:
     posts:[ {id:1,title:p1}]
     tags:[ {id:1, name:t1}, {id:2, name:t2} ]
     post_tag :[{post_id:1,tag_id:1},{post_id:1,tag_id:2}]

     select posts.id, posts.name from posts
     left join post_tags on posts.id = post_tag.post_id
     left join tags on post_tag.tag_id = tags.id
     where tags.id > 0;

     results: posts:[ {id:1,title:p1},{id:1,title:p1}]
     * limitation on distinct:
     for sql server, can not distinct on Text field

     soution/work around:
     create two query for an entity, one for list, one for detail(query by ID), only put Text field to Detail query
     */
    private async Task<Record[]> ListWithAction(QueryContext ctx, Span span, StrArgs args, CancellationToken ct = default)
    {
        var (query, filters, sorts, pagination) = ctx;
        var validSpan = span.ToValid(query.Entity.Attributes, resolver).Ok();

        var hookParam = new QueryPreGetListArgs(query,  [..filters], query.Sorts, validSpan,
            pagination.PlusLimitOne());
        var res = await hook.QueryPreGetList.Trigger(provider, hookParam);
        Record[] items;
        if (res.OutRecords is not null)
        {
            logger.LogInformation("Returning records from hook, query = {query}", ctx.Query.Name);
            items = span.ToPage(res.OutRecords, pagination.Limit);
        }
        else
        {
            var fields = query.Selection.Where(x => x.IsLocal());
            var status = PublicationStatusHelper.GetDataStatus(args);
            var kateQuery = query.Entity.ListQuery( filters, sorts, pagination.PlusLimitOne(), validSpan, fields, status);
            if (query.Distinct) kateQuery = kateQuery.Distinct();
            items = await executor.Many(kateQuery, ct);
            items = span.ToPage(items, pagination.Limit);
            if (items.Length > 0)
            {
                await LoadItems(query.Selection, args, items, ct);
            }
        }

        await LoadAsset([..query.Selection], items);
        query.Selection.SetSpan(items, query.Sorts, null);
        return items;
    }

    private async Task LoadAsset(GraphAttribute[] attributes, Record[] records)
    {
        var fields = attributes.GetAssetFields();
        if (fields.Length == 0) return;
        
        var paths = attributes.GetAllAssetPath(records);
        if (paths.Length == 0)
        {
            return;
        }

        if (fields.Length == 1 && fields[0] == nameof(Asset.Path).Camelize())
        {
            //no need to query asset table
            var assets = paths.Select(x => new Asset(x,"","","",0,"",new Dictionary<string, object>(),""));
            attributes.ReplaceAsset(records, assets.ToDictionary(x => x.Path));
        }
        else
        {
            if (!fields.Contains(nameof(Asset.Path).Camelize()))
            {
                fields =[..fields, nameof(Asset.Path).Camelize()];
            }
            
            var assetRecords = await executor.Many(Assets.GetAssetsByPaths(fields,paths));
            var assets = assetRecords
                .Select(x => x.ToObject<Asset>().Ok())
                .ToDictionary(x=>x.Path);
            attributes.ReplaceAsset(records, assets);
        }
    }

    private async Task<Record?> SingleWithAction(QueryContext ctx, StrArgs args, CancellationToken ct = default)
    {
        var (query, filters,sorts,_) = ctx;
        var res = await hook.QueryPreGetSingle.Trigger(provider,
            new QueryPreGetSingleArgs(ctx.Query,  [..filters]));
        Record? item;
        if (res.OutRecord is not null)
        {
            logger.LogInformation("Query Single: Returning records from hook, query = {query}", ctx.Query.Name);
            item = res.OutRecord;
        }
        else
        {
            var fields = query.Selection.Where(x => x.IsLocal());
            PublicationStatus? pubStatus = args.ContainsEnumKey(SpecialQueryKeys.Preview) ? null : PublicationStatus.Published;
            var kateQuery = query.Entity.SingleQuery(filters, sorts,fields ,pubStatus).Ok();
            item = await executor.Single(kateQuery, ct);
            if (item is not null)
            {
                await LoadItems(query.Selection, args, [item], ct);
            }
        }

        if (item is null) return item;
        
        query.Selection.SetSpan([item], [], null);
        await LoadAsset([..query.Selection], [item]);
        return item;
    }

    private async Task LoadItems(IEnumerable<GraphAttribute>? attrs, StrArgs args, Record[] items, CancellationToken ct)
    {
        if (attrs is null) return;
        
        foreach (var attr in attrs)
        {
            if (attr.IsCompound())
            {
                await AttachRelated(attr, args, items, ct);
            }else if (attr.IsCsv())
            {
                attr.SpreadCsv(items);
            }
        }
    }
    

    private async Task AttachRelated(GraphAttribute attr, StrArgs args, Record[] items, CancellationToken ct)
    {
        var desc = attr.GetEntityLinkDesc().Ok();
        var ids = desc.SourceAttribute.GetUniq(items);
        if (ids.Length == 0) return;

        CollectiveQueryArgs? collectionArgs = null;
        if (desc.IsCollective)
        {
            var filters = FilterHelper.ReplaceVariables(attr.Filters, args, resolver).Ok();
            var sorts = await SortHelper.ReplaceVariables(attr.Sorts, args, desc.TargetEntity, resolver,PublicationStatusHelper.GetSchemaStatus(args)).Ok();
            var fly = PaginationHelper.ResolvePagination(attr, args) ?? attr.Pagination;
            var validPagination = fly.IsEmpty()
                ? null
                : PaginationHelper.ToValid(fly, attr.Pagination, desc.TargetEntity.DefaultPageSize, false, args);
            collectionArgs = new CollectiveQueryArgs(filters,sorts,validPagination,null);
        }

        if (collectionArgs?.Pagination is null)
        {
            //get all items and no pagination
            var query = desc.GetQuery(attr.Selection.Where(x=>x.IsLocal()) ,ids, collectionArgs, 
                PublicationStatusHelper.GetDataStatus(args));
            var targetRecords = await executor.Many(query, ct);
            
            if (targetRecords.Length > 0)
            {
                var groups = targetRecords.GroupBy(x => desc.TargetAttribute.GetValueOrLookup(x), x => x);
                foreach (var group in groups)
                {
                    var sourceItems = items.Where(x => x[desc.SourceAttribute.Field].Equals(group.Key));
                    object? targetValues = desc.IsCollective ? group.ToArray() : group.FirstOrDefault();
                    if (targetValues is null) continue;
                    foreach (var item in sourceItems)
                    {
                        item[attr.Field] = targetValues;
                    }
                }
                await LoadItems(attr.Selection, args, targetRecords, ct);
            }
        }
        else
        {
            var fields = attr.Selection.Where(x => x.IsLocal()).ToArray();
            var pubStatus = PublicationStatusHelper.GetDataStatus(args);
            var plusOneArgs = collectionArgs with { Pagination = collectionArgs.Pagination.PlusLimitOne() };
            foreach (var id in ids)
            {
                
                var query = desc.GetQuery(fields, ids,plusOneArgs, pubStatus);
                var targetRecords = await executor.Many(query, ct);

                targetRecords = new Span().ToPage(targetRecords, collectionArgs.Pagination.Limit);
                if (targetRecords.Length > 0)
                {
                    var sourceItems = items.Where(x => x[desc.SourceAttribute.Field].Equals(id.ObjectValue));
                    foreach (var item in sourceItems)
                    {
                        item[attr.Field] = targetRecords;
                    }
                    await LoadItems(attr.Selection, args, targetRecords, ct);
                }
            }
        }
    }

    private record QueryContext(LoadedQuery Query, ValidFilter[] Filters, ValidSort[] Sorts, ValidPagination Pagination);

    private async Task<QueryContext> FromSavedQuery(
        string name, Pagination? pagination,  bool haveCursor, StrArgs args, CancellationToken token =default)
    {
        var status = PublicationStatusHelper.GetSchemaStatus(args);
        var query = await schemaSvc.ByNameAndCache(name, status,token);
        ResultExt.Ensure(query.VerifyVariable(args));
        
        if (status != PublicationStatus.Published)
        {
            //remove preview variables
            foreach (var (key, value) in args)
            {
                if ("{" + key + "}" == value)
                {
                    args.Remove(key);
                }
            }
        }
        return await GetQueryContext(query, pagination,haveCursor,args);
    }

    private async Task<QueryContext> FromGraphQlRequest(GraphQlRequestDto dto, StrArgs args)
    {
         var loadedQuery = await schemaSvc.ByGraphQlRequest(dto.Query,dto.Fields);
         return await GetQueryContext(loadedQuery, null,false,args);
    }

    private async Task<QueryContext> GetQueryContext(LoadedQuery query, Pagination? fly, bool haveCursor, StrArgs args)
    {
        var validPagination = PaginationHelper.ToValid(fly, query.Pagination, query.Entity.DefaultPageSize, haveCursor,args);
        var sort =(await SortHelper.ReplaceVariables(query.Sorts,args, query.Entity, resolver,PublicationStatusHelper.GetSchemaStatus(args))).Ok();
        var filters = FilterHelper.ReplaceVariables(query.Filters,args, resolver).Ok();
        return new QueryContext(query, filters, sort,validPagination);
    }
}