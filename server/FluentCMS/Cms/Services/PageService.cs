using FluentCMS.Utils.DictionaryExt;
using FluentCMS.Utils.PageRender;
using FluentCMS.Core.Descriptors;
using FluentCMS.Utils.ResultExt;
using FluentResults;
using HandlebarsDotNet;
using HtmlAgilityPack;
using Microsoft.AspNetCore.WebUtilities;

namespace FluentCMS.Cms.Services;

public sealed class PageService(ILogger<PageService> logger,ISchemaService schemaSvc, IQueryService querySvc, PageTemplate template) : IPageService
{
    public async Task<string> GetDetail(string name, string param, StrArgs strArgs, CancellationToken token)
    {
        //detail page format <pageName>/{<routerName>}, not know the exact page name now, match with prefix '/{'. 
        var ctx = ((await GetContext(name+ "/{" , true,token))).Ok().ToPageContext();
        strArgs = GetLocalPaginationArgs(ctx, strArgs); 
        
        var routerName =ctx.Page.Name.Split("/").Last()[1..^1]; // remove '{' and '}'
        strArgs[routerName] = param;

        var data = string.IsNullOrWhiteSpace(ctx.Page.Query)
            ? new Dictionary<string, object>()
            : await querySvc.SingleWithAction(ctx.Page.Query, strArgs, token)
              ?? throw new ResultException($"not find data by of {param}");
        
        return await RenderPage(ctx, data, strArgs, token);
    }

    public async Task<string> Get(string name, StrArgs strArgs, CancellationToken token)
    {
        if ((await GetContext(name, false, token)).Try(out var ctx, out var error))
        {
            return await RenderPage(ctx.ToPageContext(), new Dictionary<string, object>(), strArgs, token);
        }

        var msg = string.Join(",", error!.Select(x => x.Message));
        if (name != "home")
        {
            throw new ResultException(msg);
        }

        logger.LogError("Fail to load page [{page}], err: {err}", name, msg);
        return $"""
                <a href="/admin">Log in to Admin</a><br/>
                <a href="/schema">Go to Schema Builder</a>
                """;
    }

    public async Task<string> GetPart(string partStr, CancellationToken token)
    {
        var part = PagePartHelper.Parse(partStr) ?? throw new ResultException("Invalid Partial Part");
        var ctx = (await GetContext(part.Page, false, token)).Ok().ToPartialContext(part.NodeId);

        var cursor = new Span(part.First, part.Last);
        var args = QueryHelpers.ParseQuery(part.DataSource.QueryString);

        Record[] items;
        if (!string.IsNullOrWhiteSpace(part.DataSource.Query))
        {
            var pagination = new Pagination(null, part.DataSource.Limit.ToString());
            items = await querySvc.ListWithAction(part.DataSource.Query, cursor, pagination, args, token);
        }
        else
        {
            items = await querySvc.Partial(ctx.Page.Query!, part.DataSource.Field, cursor, part.DataSource.Limit, args, token);
        }

        var flatField = RenderUtil.Flat(part.DataSource.Field);
        var data = new Dictionary<string, object>
        {
            [flatField] = items
        };
        TagPagination(data, items, part);
        
        ctx.Node.SetPaginationTemplate(flatField, part.DataSource.PageMode);
        var html = part.DataSource.PageMode == PageMode.Button
            ? ctx.Node.OuterHtml // for button pagination, replace the div 
            : ctx.Node.InnerHtml; // for infinite screen, append to original div
        var render = Handlebars.Compile(html);
        return render(data);
    }

    private async Task<string> RenderPage(PageContext ctx, Record data, StrArgs args, CancellationToken token)
    {
        await LoadRelatedData(data, args, ctx.Nodes, token);
        TagPagination(ctx, data,args);

        foreach (var repeatNode in ctx.Nodes)
        {
            repeatNode.HtmlNode.SetPaginationTemplate(repeatNode.DataSource.Field, repeatNode.DataSource.PageMode);
        }

        var title = Handlebars.Compile(ctx.Page.Title)(data);
        var body = ctx.HtmlDocument.RenderBody(data);
        return template.Build(title, body, ctx.Page.Css);
    }

    private static StrArgs GetLocalPaginationArgs(PageContext ctx,StrArgs strArgs)
    {
        var ret = new StrArgs(strArgs);
        foreach (var node in ctx.Nodes.Where(x => 
                string.IsNullOrWhiteSpace(x.DataSource.Query) && (x.DataSource.Offset > 0 || x.DataSource.Limit > 0)))
        {
            ret[node.DataSource.Field + PaginationConstants.OffsetSuffix] = node.DataSource.Offset.ToString();
            ret[node.DataSource.Field + PaginationConstants.LimitSuffix] = node.DataSource.Limit.ToString();
        }
        return ret;
    }

    private async Task LoadRelatedData(Record data, StrArgs args, DataNode[] nodes, CancellationToken token)
    {
        foreach (var node in nodes.Where(x => !string.IsNullOrWhiteSpace(x.DataSource.Query)))
        {
            var pagination = new Pagination(node.DataSource.Offset.ToString(), node.DataSource.Limit.ToString());
            var result = await querySvc.ListWithAction(node.DataSource.Query, new Span(), pagination, node.MergeArgs(args), token);
            data[node.DataSource.Field] = result;
        }
    }

    private static void TagPagination(PageContext ctx, Record data, StrArgs args)
    {
        foreach (var node in ctx.Nodes.Where(x => x.DataSource.Offset > 0 || x.DataSource.Limit > 0))
        {
            if (data.GetValueByPath<Record[]>(node.DataSource.Field, out var value))
            {
                var nodeWithArg = node with
                {
                    DataSource = node.DataSource with { QueryString = node.MergeArgs(args).ToQueryString() }
                };
                TagPagination(data, value!, nodeWithArg.ToPagePart(ctx.Page.Name));
            }
        }
    }

    private static void TagPagination(Record data, Record[] items, PagePart token)
    {
        if (SpanHelper.HasPrevious(items))
        {
            data[RenderUtil.FirstAttrTag(token.DataSource.Field) ] = PagePartHelper.ToString((token with { First = SpanHelper.FirstCursor(items), Last = ""}));
        }

        if (SpanHelper.HasNext(items))
        {
            data[RenderUtil.LastAttrTag(token.DataSource.Field)] = PagePartHelper.ToString((token with { Last = SpanHelper.LastCursor(items),First = ""}));
        }
    }
    record Context(Page Page, HtmlDocument Doc)
    {
        public PartialContext ToPartialContext(string nodeId) => new (Page, Doc.GetElementbyId(nodeId));
    
        public PageContext ToPageContext() => new (Page, Doc, Doc.GetDataNodes().Ok());
    }

    record PageContext(Page Page, HtmlDocument HtmlDocument, DataNode[] Nodes);
    
    record PartialContext(Page Page, HtmlNode Node);
    
    private async Task<Result<Context>> GetContext(string name, bool matchPrefix, CancellationToken token)
    {
        var schema = matchPrefix
            ? await schemaSvc.GetByNamePrefixDefault(name, SchemaType.Page, token)
            : await schemaSvc.GetByNameDefault(name, SchemaType.Page, token);
        
        if (schema == null) { return Result.Fail("Can not find schema"); }

        var page = schema.Settings.Page!;
        var doc = new HtmlDocument();
        doc.LoadHtml(page.Html);
        return new Context(page, doc);
    }
}

