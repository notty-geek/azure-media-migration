﻿using AMSMigrate.Contracts;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace AMSMigrate.Azure
{
    internal class AzureStorageUploader : IFileUploader
    {
        private readonly AssetOptions _options;
        private readonly ILogger _logger;
        private readonly BlobServiceClient _blobServiceClient;

        public AzureStorageUploader(
            AssetOptions options,
            TokenCredential credential, 
            ILogger<AzureStorageUploader> logger)
        {
            _options = options;
            _logger = logger;
            if (!Uri.TryCreate(options.StorageAccount, UriKind.Absolute, out var storageUri))
            {
                storageUri = new Uri($"https://{options.StorageAccount}.blob.core.windows.net");
            }
            _blobServiceClient = new BlobServiceClient(storageUri, credential);
        }

        public async Task UploadAsync(
            string containerName,
            string fileName,
            Stream content,
            IProgress<long> progress,
            CancellationToken cancellationToken = default)
        {
            _logger.LogTrace(
                "Uploading to {fileName} in container {container} of account: {account}...",
                fileName, containerName, _blobServiceClient.AccountName);
            var container = _blobServiceClient.GetBlobContainerClient(containerName);
            var outputBlob = container.GetBlockBlobClient(fileName);
            var options = new BlobUploadOptions
            {
                ProgressHandler = progress,
                Conditions = new BlobRequestConditions
                {
                    IfNoneMatch = _options.OverWrite ? null : ETag.All
                }
            };
            await outputBlob.UploadAsync(content, options, cancellationToken: cancellationToken);
        }

        public async Task UploadBlobAsync(
            string containerName,
            string fileName,
            BlockBlobClient blob,
            CancellationToken cancellationToken)
        {
            var container = _blobServiceClient.GetBlobContainerClient(containerName);
            var outputBlob = container.GetBlockBlobClient(fileName);
            await outputBlob.StartCopyFromUriAsync(blob.Uri, cancellationToken: cancellationToken);
        }
    }
}
