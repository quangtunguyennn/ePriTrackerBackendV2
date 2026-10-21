





internal class SqlServerStorageOptions : Hangfire.SqlServer.SqlServerStorageOptions
{
    public TimeSpan CommandBatchMaxTimeout { get; set; }
    public TimeSpan SlidingInvisibilityTimeout { get; set; }
    public TimeSpan QueuePollInterval { get; set; }
    public bool UseRecommendedIsolationLevel { get; set; }
    public bool DisableGlobalLocks { get; set; }
}