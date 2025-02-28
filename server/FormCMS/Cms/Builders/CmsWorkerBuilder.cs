using FormCMS.Cms.Workers;
using FormCMS.Infrastructure.LocalFileStore;
using FormCMS.Infrastructure.RelationDbDao;

namespace FormCMS.Cms.Builders;

public record TaskTimingSeconds(int QueryTimeout,int ExportDelay,int ImportDelay, int PublishDelay);
public static class CmsWorkerBuilder
{
    public static IServiceCollection AddWorker(
        IServiceCollection services,
        DatabaseProvider databaseProvider,
        string connectionString,
        TaskTimingSeconds? taskTimingSeconds
       ) 
    {
        taskTimingSeconds ??= new TaskTimingSeconds(60, 30, 30, 30);
        var parts = connectionString.Split(";").Where(x => !x.StartsWith("Password"));
        Console.WriteLine(
            $"""
             *********************************************************
             Adding CMS Workers
             Database : {databaseProvider} - {string.Join(";", parts)}
             TaskTimingConfig: {taskTimingSeconds}
             *********************************************************
             """);

        services.AddSingleton(new LocalFileStoreOptions(
            Path.Join(Directory.GetCurrentDirectory(), "wwwroot/files"), "/files",0, 0));
        services.AddSingleton<IFileStore,LocalFileStore>();
        
        //scoped services
        services.AddDao(databaseProvider, connectionString );
        services.AddSingleton(new KateQueryExecutorOption(taskTimingSeconds.QueryTimeout));
        services.AddScoped<KateQueryExecutor>();
        services.AddScoped<DatabaseMigrator>();
        
        services.AddSingleton(new ExportWorkerOptions(taskTimingSeconds.ExportDelay));
        services.AddHostedService<ExportWorker>();
        
        services.AddSingleton(new ImportWorkerOptions(taskTimingSeconds.ImportDelay));
        services.AddHostedService<ImportWorker>();
        
        services.AddSingleton(new DataPublishingWorkerOptions(taskTimingSeconds.PublishDelay));
        services.AddHostedService<DataPublishingWorker>();
        
        return services;
    }
    
}