// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.Configuration;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.Pipeline;

namespace CopilotChat.Shared.Documents;

/// <summary>
/// A heavily modified version of the <see cref="Microsoft.KernelMemory.Handlers.TextExtractionHandler"/> 
/// that uses Azure Document Intelligence for all supported types.
/// </summary>
public class DocumentExtractionHandler : IPipelineStepHandler
{
    private readonly IPipelineOrchestrator _orchestrator;
    private readonly DocumentAnalysisClient _docIntel;
    private readonly ILogger<DocumentExtractionHandler> _logger;

    public string StepName { get; }

    public DocumentExtractionHandler(
        string stepName,
        IPipelineOrchestrator orchestrator,
        AzureAIDocIntelConfig config,
        ILogger<DocumentExtractionHandler>? logger = null
    )
    {
        this.StepName = stepName;
        this._orchestrator = orchestrator;
        this._logger = logger ?? DefaultLogger<DocumentExtractionHandler>.Instance;
        switch (config.Auth)
        {
            case AzureAIDocIntelConfig.AuthTypes.AzureIdentity:
                this._docIntel = new(
                    new Uri(config.Endpoint),
                    new DefaultAzureCredential()
                );
                break;

            case AzureAIDocIntelConfig.AuthTypes.APIKey:
                if (string.IsNullOrEmpty(config.APIKey))
                {
                    this._logger.LogCritical("Azure AI Document Intelligence API key is empty");
                    throw new ConfigurationException("Azure AI Document Intelligence API key is empty");
                }

                this._docIntel = new(
                   new Uri(config.Endpoint),
                   new AzureKeyCredential(config.APIKey)
                );

                break;
            default:
                this._logger.LogCritical("Azure AI Document Intelligence authentication type '{0}' undefined or not supported", config.Auth);
                throw new ConfigurationException($"Azure AI Document Intelligence authentication type '{config.Auth}' undefined or not supported");
        }
    }

    /// <summary>
    /// Add the contextual decoration found in a page header.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="page"></param>
    protected static void PageHeader(StringBuilder builder, int page)
    {
       //TODO: Add Handlers for page parts and define symbols
    }

    /// <summary>
    /// Add the contextual decoration found in a page footer.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="page"></param>
    private static void PageFooter(StringBuilder builder, int page)
    {
        builder.AppendLine();
        builder.AppendLine($"PAGE {page}");
        builder.AppendLine();
    }

    /// <summary>
    /// Adds text to the builder.
    /// </summary>
    /// <remarks>
    /// Splits plain text into pages of 48 lines per page by default.
    /// Leaves other mime types untouched before adding to the builder.
    /// </remarks>
    /// <param name="builder">The content string builder</param>
    /// <param name="mimeType">The mime type of the current file</param>
    /// <param name="fileContent"></param>
    /// <returns>The mime type of file</returns>
    private static string AddText(StringBuilder builder, string mimeType, BinaryData fileContent, int lpp = 48)
    {
        if (mimeType != MimeTypes.PlainText)
        {
            builder.Append(fileContent.ToString());
            return mimeType;
        }

        int page = 1;
        var lines = fileContent.ToString().Split('\n');

        for (int i = 0; i < lines.Length; i += lpp)
        {
            PageHeader(builder, page);
            foreach (var ln in lines.Skip(i).Take(lpp))
            {
                builder.AppendLine(ln);
            }
            PageFooter(builder, page++);
        }
        return MimeTypes.PlainText;

    }

    /// <summary>
    /// Adds Azure Document Intelligence pages to the builder.
    /// </summary>
    /// <param name="builder">The content string builder</param>
    /// <param name="pages">Analyzed pages</param>
    private static void AddPages(StringBuilder builder, IReadOnlyList<DocumentPage> pages)
    {
        foreach (var page in pages)
        {
            PageHeader(builder, page.PageNumber);
            foreach (var line in page.Lines)
            {
                builder.AppendLine(line.Content);
            }
            PageFooter(builder, page.PageNumber);
        }
    }

    public async Task<(bool success, DataPipeline updatedPipeline)> InvokeAsync(DataPipeline pipeline, CancellationToken cancellationToken = default)
    {
        foreach (var file in pipeline.Files)
        {
            if (file.AlreadyProcessedBy(this)) { continue; }

            var sourceFile = file.Name;
            BinaryData fileContent = await this._orchestrator.ReadFileAsync(pipeline, sourceFile, cancellationToken).ConfigureAwait(false);

            string extractType = MimeTypes.PlainText;
            StringBuilder strBuilder = new();
            switch (file.MimeType)
            {
                case MimeTypes.PlainText:
                case MimeTypes.MarkDown:
                case MimeTypes.Json:
                    this._logger.LogDebug("Extracting text from `{}` file `{}`", file.MimeType, file.Name);
                    extractType = AddText(strBuilder, file.MimeType, fileContent);
                    break;

                case MimeTypes.MsWord:
                case MimeTypes.MsPowerPoint:
                case MimeTypes.MsExcel:
                case MimeTypes.Pdf:
                case MimeTypes.ImageJpeg:
                case MimeTypes.ImagePng:
                case MimeTypes.ImageTiff:

                    this._logger.LogDebug("Document Intelligence extract from `{}` file `{}`", file.MimeType, file.Name);
                    try
                    {
                        var operation = await this._docIntel.AnalyzeDocumentAsync(
                            WaitUntil.Completed,
                            "prebuilt-read",
                            fileContent.ToStream(),
                            cancellationToken: cancellationToken
                        ).ConfigureAwait(false);

                        // Wait for the result
                        AnalyzeResult operationResponse = await operation.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);

                        AddPages(strBuilder, operationResponse.Pages);

                    }
                    catch (Exception ex)
                    {
                        this._logger.LogWarning(ex, "Error processing {}", file.Name);
                        throw ex;
                    }

                    break;
                default:
                    continue;
            }

            if (strBuilder.Length == 0) { continue; }
            var extracted = $"{file.Name}.extract.txt";

            this._logger.LogDebug("Saving extracted text file {}", extracted);
            await this._orchestrator.WriteFileAsync(
                pipeline,
                extracted,
                new BinaryData(strBuilder.ToString()),
                cancellationToken
            ).ConfigureAwait(false);

            var extractedDetails = new DataPipeline.GeneratedFileDetails
            {
                Id = Guid.NewGuid().ToString("N"),
                ParentId = file.Id,
                Name = extracted,
                Size = strBuilder.Length,
                MimeType = extractType,
                ArtifactType = DataPipeline.ArtifactTypes.ExtractedText,
                Tags = pipeline.Tags,
            };
            extractedDetails.MarkProcessedBy(this);

            file.GeneratedFiles.Add(extracted, extractedDetails);
        }
        return (true, pipeline);
    }


}
