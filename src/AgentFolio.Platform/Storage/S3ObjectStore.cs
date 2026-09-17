using Amazon.S3;
using Amazon.S3.Model;

namespace AgentFolio.Platform.Storage;

/// <summary>
/// AWSSDK.S3-backed IObjectStore. Configuration comes exclusively from environment
/// variables (service-url style, path-style addressing for MinIO compatibility):
///   AGENTFOLIO_S3_ENDPOINT, AGENTFOLIO_S3_BUCKET,
///   AGENTFOLIO_S3_ACCESS_KEY, AGENTFOLIO_S3_SECRET_KEY.
/// A missing AGENTFOLIO_S3_ENDPOINT (or bucket) throws InvalidOperationException
/// from the constructor — there is no silent local fallback.
/// </summary>
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    public const string EndpointEnvVar = "AGENTFOLIO_S3_ENDPOINT";
    public const string BucketEnvVar = "AGENTFOLIO_S3_BUCKET";
    public const string AccessKeyEnvVar = "AGENTFOLIO_S3_ACCESS_KEY";
    public const string SecretKeyEnvVar = "AGENTFOLIO_S3_SECRET_KEY";
    /// <summary>
    /// sigv4 scope region. Self-hosted stores REJECT the us-east-1 default
    /// (garage: "Authorization header malformed, unexpected scope" — measured
    /// live 2026-09-17); there is no correct guess, so it must be explicit.
    /// </summary>
    public const string RegionEnvVar = "AGENTFOLIO_S3_REGION";

    private readonly IAmazonS3 _client;
    private readonly string _bucket;

    public S3ObjectStore() : this(
        Environment.GetEnvironmentVariable(EndpointEnvVar),
        Environment.GetEnvironmentVariable(BucketEnvVar),
        Environment.GetEnvironmentVariable(AccessKeyEnvVar),
        Environment.GetEnvironmentVariable(SecretKeyEnvVar),
        Environment.GetEnvironmentVariable(RegionEnvVar))
    {
    }

    public S3ObjectStore(string? endpoint, string? bucket, string? accessKey, string? secretKey, string? region)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException(
                $"Environment variable {EndpointEnvVar} is not set. Object storage is mandatory — set it to the S3/MinIO service URL (no silent fallback).");
        if (string.IsNullOrWhiteSpace(bucket))
            throw new InvalidOperationException(
                $"Environment variable {BucketEnvVar} is not set. Set it to the target bucket name.");

        _bucket = bucket;
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true, // MinIO-compatible path-style addressing
            UseHttp = endpoint.StartsWith("http://", StringComparison.Ordinal),
            AuthenticationRegion = string.IsNullOrWhiteSpace(region)
                ? throw new InvalidOperationException(
                    $"Environment variable {RegionEnvVar} is not set. Self-hosted stores (garage/MinIO) reject the us-east-1 signing scope — set the store's own region (garage: s3_region from its config).")
                : region,
            // SDK v4's default integrity protection (CRC32 checksum + signed
            // streaming trailer) is not implemented by garage/MinIO-class stores:
            // every PUT dies "Bad request: Invalid payload signature" (measured
            // live vs garage 2.1.0, 2026-09-17). WHEN_REQUIRED keeps integrity
            // checks where the S3 API demands them and sends plain sigv4 payloads
            // otherwise — accepted by self-hosted stores AND real AWS.
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
        };
        _client = new AmazonS3Client(
            accessKey ?? throw new InvalidOperationException(
                $"Environment variable {AccessKeyEnvVar} is not set."),
            secretKey ?? throw new InvalidOperationException(
                $"Environment variable {SecretKeyEnvVar} is not set."),
            config);
    }

    public async Task PutAsync(string key, Stream data, string contentType, CancellationToken ct = default)
    {
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = data,
            ContentType = contentType,
            // Payload signing stays ENABLED: AWSSDK rejects DisablePayloadSigning
            // over plain http:// ("must be sent over HTTPS"), which is how every
            // homelab/tailnet S3 (garage/MinIO) endpoint ships. Measured live:
            // first real garage deploy 500'd every upload until this was removed.
        }, ct);
    }

    public async Task<Stream?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucket,
                Key = key,
            }, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> DeletePrefixAsync(string prefix, CancellationToken ct = default)
    {
        var deletedAny = false;
        string? continuationToken = null;
        do
        {
            var list = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix,
                ContinuationToken = continuationToken,
            }, ct);

            if (list.S3Objects.Count > 0)
            {
                await _client.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = _bucket,
                    Quiet = true,
                    Objects = [.. list.S3Objects.Select(o => new KeyVersion { Key = o.Key })],
                }, ct);
                deletedAny = true;
            }
            continuationToken = list.IsTruncated == true ? list.NextContinuationToken : null;
        }
        while (continuationToken is not null);
        return deletedAny;
    }

    public async Task<long?> StatAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var meta = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key,
            }, ct);
            return meta.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();

    public async Task<IReadOnlyList<string>> ListByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        var keys = new List<string>();
        string? token = null;
        do
        {
            var resp = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix,
                ContinuationToken = token,
            }, ct);
            keys.AddRange((resp.S3Objects ?? []).Select(o => o.Key));
            token = resp.NextContinuationToken;
        } while (token is not null);
        return keys;
    }
}
