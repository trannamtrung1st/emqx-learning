namespace EmqxLearning.MqttListener;

public static class Constants
{
    public static class ResiliencePipelines
    {
        public const string ConnectionErrors = nameof(ConnectionErrors);
        public const string TransientErrors = nameof(TransientErrors);
    }

    public static class LimiterNames
    {
        public const string TaskLimiter = nameof(TaskLimiter);
        public const string SizeLimiter = nameof(SizeLimiter);
    }
}