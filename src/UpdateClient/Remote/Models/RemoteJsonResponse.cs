namespace UpdateClient.Remote.Models
{
    internal sealed class RemoteJsonResponse<T>
    {
        public string Url { get; set; }

        public T Value { get; set; }
    }
}
