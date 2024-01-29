// Copyright (c) Microsoft. All rights reserved.

using System;
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

namespace CopilotChat.Shared.Ocr;

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

    private static void PageFooter(StringBuilder strBldr, int page)
    {
        strBldr.AppendLine();
        strBldr.AppendLine($"PAGE {page}");
        strBldr.AppendLine();
    }

    /// <summary>
    /// Creates 48 line pages with a footer for each page
    /// from the text content of the file. Otherwise it 
    /// returns the mime type of the text content and and adds it.
    /// </summary>
    /// <param name="mimeType"></param>
    /// <param name="fileContent"></param>
    /// <param name="strBldr"></param>
    /// <returns></returns>
    private static string BuildStringFromText(string mimeType, BinaryData fileContent, StringBuilder strBldr)
    {
        if (mimeType != MimeTypes.PlainText)
        {
            strBldr.Append(fileContent.ToString());
            return mimeType;
        }

        int lpp = 48;
        var lines = fileContent.ToString().Split('\n');

        int page = 1;
        for (int i = 0; i < lines.Length; i += lpp)
        {
            foreach (var ln in lines.Skip(i).Take(lpp))
            {
                strBldr.AppendLine(ln);
            }
            PageFooter(strBldr, page++);
        }
        return MimeTypes.PlainText;
    }

    public async Task<(bool success, DataPipeline updatedPipeline)> InvokeAsync(DataPipeline pipeline, CancellationToken cancellationToken = default)
    {
        foreach (var file in pipeline.Files)
        {
            if (file.AlreadyProcessedBy(this)) { continue; }

            var sourceFile = file.Name;
            BinaryData fileContent = await this._orchestrator.ReadFileAsync(pipeline, sourceFile, cancellationToken).ConfigureAwait(false);

            string extractType = MimeTypes.PlainText;
            var strBuilder = new StringBuilder();
            switch (file.MimeType)
            {
                case MimeTypes.PlainText:
                case MimeTypes.MarkDown:
                case MimeTypes.Json:
                    this._logger.LogDebug("Extracting text from `{}` file `{}`", file.MimeType, file.Name);
                    extractType = BuildStringFromText(file.MimeType, fileContent, strBuilder);
                    break;

                case MimeTypes.MsWord:
                case MimeTypes.MsPowerPoint:
                case MimeTypes.MsExcel:
                case MimeTypes.Pdf:
                case MimeTypes.ImageJpeg:
                case MimeTypes.ImagePng:
                case MimeTypes.ImageTiff:

                    this._logger.LogDebug("DocIntel extract from `{}` file `{}`", file.MimeType, file.Name);
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

                        foreach (var page in operationResponse.Pages)
                        {
                            foreach (var line in page.Lines)
                            {
                                strBuilder.AppendLine(line.Content);
                            }
                            PageFooter(strBuilder, page.PageNumber);
                        }

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
