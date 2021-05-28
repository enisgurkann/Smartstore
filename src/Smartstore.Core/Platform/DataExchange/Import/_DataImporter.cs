﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.Extensions.Logging;
using Smartstore.Collections;
using Smartstore.Core.Common.Settings;
using Smartstore.Core.DataExchange.Csv;
using Smartstore.Core.DataExchange.Import.Events;
using Smartstore.Core.DataExchange.Import.Internal;
using Smartstore.Core.Identity;
using Smartstore.Core.Localization;
using Smartstore.Core.Messaging;
using Smartstore.Core.Security;
using Smartstore.Engine;
using Smartstore.Net.Mail;
using Smartstore.Utilities;

namespace Smartstore.Core.DataExchange.Import
{
    public partial class DataImporter : IDataImporter
    {
        private readonly ICommonServices _services;
        private readonly ILifetimeScopeAccessor _scopeAccessor;
        private readonly IImportProfileService _importProfileService;
        private readonly IEmailAccountService _emailAccountService;
        private readonly IMailService _mailService;
        private readonly ContactDataSettings _contactDataSettings;

        public DataImporter(
            ICommonServices services,
            ILifetimeScopeAccessor scopeAccessor,
            IImportProfileService importProfileService,
            IEmailAccountService emailAccountService,
            IMailService mailService,
            ContactDataSettings contactDataSettings)
        {
            _services = services;
            _scopeAccessor = scopeAccessor;
            _importProfileService = importProfileService;
            _emailAccountService = emailAccountService;
            _mailService = mailService;
            _contactDataSettings = contactDataSettings;
        }

        public Localizer T { get; set; } = NullLocalizer.Instance;

        public async Task ImportAsync(DataImportRequest request, CancellationToken cancelToken = default)
        {
            Guard.NotNull(request, nameof(request));
            Guard.NotNull(cancelToken, nameof(cancelToken));

            DataImporterContext ctx = null;

            try
            {
                var profile = await _services.DbContext.ImportProfiles.FindByIdAsync(request.ProfileId, false, cancelToken);
                if (!(profile?.Enabled ?? false))
                    return;

                ctx = await CreateImporterContext(request, profile, cancelToken);

                if (!request.HasPermission && !await HasPermission())
                {
                    throw new SmartException("You do not have permission to perform the selected import.");
                }

                var context = ctx.ExecuteContext;
                var files = await _importProfileService.GetImportFilesAsync(profile, profile.ImportRelatedData);
                var fileGroups = files.ToMultimap(x => x.RelatedType, x => x);

                ctx.Log.Info(CreateLogHeader(profile, fileGroups));
                await _services.EventPublisher.PublishAsync(new ImportExecutingEvent(context), cancelToken);

                foreach (var fileGroup in fileGroups)
                {
                    context.Result = ctx.Results[fileGroup.Key?.ToString()?.EmptyNull()] = new();

                    foreach (var file in fileGroup.Value)
                    {
                        if (context.Abort == DataExchangeAbortion.Hard)
                            break;

                        if (!file.File.Exists)
                            throw new SmartException($"File does not exist {file.File.SubPath}.");

                        try
                        {
                            var csvConfiguration = file.IsCsv
                                ? (new CsvConfigurationConverter().ConvertFrom<CsvConfiguration>(profile.FileTypeConfiguration) ?? CsvConfiguration.ExcelFriendlyConfiguration)
                                : CsvConfiguration.ExcelFriendlyConfiguration;

                            using var stream = file.File.OpenRead();

                            context.File = file;
                            context.ColumnMap = file.RelatedType.HasValue ? new ColumnMap() : ctx.ColumnMap;
                            context.DataTable = LightweightDataTable.FromFile(
                                file.File.Name,
                                stream,
                                stream.Length,
                                csvConfiguration,
                                profile.Skip,
                                profile.Take > 0 ? profile.Take : int.MaxValue);

                            var segmenter = new ImportDataSegmenter(context.DataTable, context.ColumnMap);

                            context.DataSegmenter = segmenter;
                            context.Result.TotalRecords = segmenter.TotalRows;

                            while (context.Abort == DataExchangeAbortion.None && segmenter.ReadNextBatch())
                            {
                                using var batchScope = _scopeAccessor.LifetimeScope.BeginLifetimeScope();

                                var importerFactory = batchScope.Resolve<Func<ImportEntityType, IEntityImporter>>();
                                var importer = importerFactory(profile.EntityType);

                                await importer.ExecuteAsync(context, cancelToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            context.Abort = DataExchangeAbortion.Hard;
                            context.Result.AddError(ex, $"The importer failed: {ex.ToAllMessages()}.");
                        }
                        finally
                        {
                            context.Result.EndDateUtc = DateTime.UtcNow;

                            if (context.IsMaxFailures)
                                context.Result.AddWarning("Import aborted. The maximum number of failures has been reached.");

                            if (ctx.CancelToken.IsCancellationRequested)
                                context.Result.AddWarning("Import aborted. A cancellation has been requested.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ctx?.Log?.ErrorsAll(ex);
            }
            finally
            {
                await Finalize(ctx);
            }

            cancelToken.ThrowIfCancellationRequested();
        }

        private async Task Finalize(DataImporterContext ctx)
        {
            if (ctx == null)
                return;

            try
            {
                await _services.EventPublisher.PublishAsync(new ImportExecutedEvent(ctx.ExecuteContext), ctx.CancelToken);
            }
            catch (Exception ex)
            {
                ctx.Log?.ErrorsAll(ex);
            }

            var profile = await _services.DbContext.ImportProfiles.FindByIdAsync(ctx.Request.ProfileId, true, ctx.CancelToken);

            try
            {
                await SendCompletionEmail(profile, ctx);
            }
            catch (Exception ex)
            {
                ctx.Log?.ErrorsAll(ex);
            }

            try
            {
                LogResults(profile, ctx);
            }
            catch (Exception ex)
            {
                ctx.Log?.ErrorsAll(ex);
            }

            try
            {
                if (ctx.Results.TryGetValue(string.Empty, out var result))
                {
                    profile.ResultInfo = XmlHelper.Serialize(result.Clone());

                    await _services.DbContext.SaveChangesAsync(ctx.CancelToken);
                }
            }
            catch (Exception ex)
            {
                ctx.Log?.ErrorsAll(ex);
            }

            try
            {
                ctx.Request.CustomData.Clear();
                ctx.Results.Clear();
                ctx.Log = null;
            }
            catch (Exception ex)
            {
                ctx.Log?.ErrorsAll(ex);
            }
        }

        #region Utilities

        private async Task SendCompletionEmail(ImportProfile profile, DataImporterContext ctx)
        {
            var emailAccount = _emailAccountService.GetDefaultEmailAccount();
            var result = ctx.ExecuteContext.Result;
            var store = _services.StoreContext.CurrentStore;
            var storeInfo = $"{store.Name} ({store.Url})";

            using var psb = StringBuilderPool.Instance.Get(out var body);

            body.Append(T("Admin.DataExchange.Import.CompletedEmail.Body", storeInfo));

            if (result.LastError.HasValue())
            {
                body.AppendFormat("<p style=\"color: #B94A48;\">{0}</p>", result.LastError);
            }

            body.Append("<p>");

            body.AppendFormat("<div>{0}: {1} &middot; {2}: {3}</div>",
                T("Admin.Common.TotalRows"), result.TotalRecords,
                T("Admin.Common.Skipped"), result.SkippedRecords);

            body.AppendFormat("<div>{0}: {1} &middot; {2}: {3}</div>",
                T("Admin.Common.NewRecords"), result.NewRecords,
                T("Admin.Common.Updated"), result.ModifiedRecords);

            body.AppendFormat("<div>{0}: {1} &middot; {2}: {3}</div>",
                T("Admin.Common.Errors"), result.Errors,
                T("Admin.Common.Warnings"), result.Warnings);

            body.Append("</p>");

            var message = new MailMessage
            {
                From = new(emailAccount.Email, emailAccount.DisplayName),
                Subject = T("Admin.DataExchange.Import.CompletedEmail.Subject").Value.FormatInvariant(profile.Name),
                Body = body.ToString()
            };

            if (_contactDataSettings.WebmasterEmailAddress.HasValue())
            {
                message.To.Add(new(_contactDataSettings.WebmasterEmailAddress));
            }

            if (!message.To.Any() && _contactDataSettings.CompanyEmailAddress.HasValue())
            {
                message.To.Add(new(_contactDataSettings.CompanyEmailAddress));
            }

            if (!message.To.Any())
            {
                message.To.Add(new(emailAccount.Email, emailAccount.DisplayName));
            }

            await using var client = await _mailService.ConnectAsync(emailAccount);
            await client.SendAsync(message, ctx.CancelToken);

            //_db.QueuedEmails.Add(new QueuedEmail
            //{
            //    From = emailAccount.Email,
            //    To = message.To.First().Address,
            //    Subject = message.Subject,
            //    Body = message.Body,
            //    CreatedOnUtc = DateTime.UtcNow,
            //    EmailAccountId = emailAccount.Id,
            //    SendManually = true
            //});
            //await _db.SaveChangesAsync();
        }

        private async Task<DataImporterContext> CreateImporterContext(DataImportRequest request, ImportProfile profile, CancellationToken cancelToken)
        {
            // TODO: (mg) (core) setup file logger for data import.
            ILogger logger = null;

            var executeContext = new ImportExecuteContext(T("Admin.DataExchange.Import.ProgressInfo"), cancelToken)
            {
                Request = request,
                Log = logger,
                UpdateOnly = profile.UpdateOnly,
                KeyFieldNames = profile.KeyFieldNames.SplitSafe(",").ToArray(),
                ImportDirectory = await _importProfileService.GetImportDirectoryAsync(profile),
                ExtraData = XmlHelper.Deserialize<ImportExtraData>(profile.ExtraData)
            };

            return new DataImporterContext
            {
                Request = request,
                CancelToken = cancelToken,
                Log = logger,
                ColumnMap = new ColumnMapConverter().ConvertFrom<ColumnMap>(profile.ColumnMapping) ?? new ColumnMap(),
                ExecuteContext = executeContext
            };
        }

        private string CreateLogHeader(ImportProfile profile, Multimap<RelatedEntityType?, ImportFile> files)
        {
            var executingCustomer = _services.WorkContext.CurrentCustomer;

            using var psb = StringBuilderPool.Instance.Get(out var sb);

            sb.AppendLine();
            sb.AppendLine(new string('-', 40));
            sb.AppendLine("Smartstore: v." + SmartstoreVersion.CurrentFullVersion);
            sb.AppendLine("Import profile: " + profile.Name);
            sb.AppendLine(profile.Id == 0 ? " (transient)" : $" (ID {profile.Id})");

            foreach (var fileGroup in files)
            {
                var entityName = fileGroup.Key.HasValue ? fileGroup.Key.Value.ToString() : profile.EntityType.ToString();
                var fileNames = string.Join(", ", fileGroup.Value.Select(x => x.File.Name));
                sb.AppendLine($"{entityName} files: {fileNames}");
            }

            sb.Append("Executed by: " + (executingCustomer.Email.HasValue() ? executingCustomer.Email : executingCustomer.SystemName));

            return sb.ToString();
        }

        private static void LogResults(ImportProfile profile, DataImporterContext ctx)
        {
            using var psb = StringBuilderPool.Instance.Get(out var sb);

            foreach (var item in ctx.Results)
            {
                var result = item.Value;
                var entityName = item.Key.HasValue() ? item.Key : profile.EntityType.ToString();

                sb.Clear();
                sb.AppendLine();
                sb.AppendLine(new string('-', 40));
                sb.AppendLine("Object:         " + entityName);
                sb.AppendLine("Started:        " + result.StartDateUtc.ToLocalTime());
                sb.AppendLine("Finished:       " + result.EndDateUtc.ToLocalTime());
                sb.AppendLine("Duration:       " + (result.EndDateUtc - result.StartDateUtc).ToString("g"));
                sb.AppendLine("Rows total:     " + result.TotalRecords);
                sb.AppendLine("Rows processed: " + result.AffectedRecords);
                sb.AppendLine("Rows imported:  " + result.NewRecords);
                sb.AppendLine("Rows updated:   " + result.ModifiedRecords);
                sb.AppendLine("Warnings:       " + result.Warnings);
                sb.Append("Errors:         " + result.Errors);
                ctx.Log.Info(sb.ToString());

                foreach (var message in result.Messages)
                {
                    if (message.MessageType == ImportMessageType.Error)
                    {
                        ctx.Log.Error(new Exception(message.FullMessage), message.ToString());
                    }
                    else if (message.MessageType == ImportMessageType.Warning)
                    {
                        ctx.Log.Warn(message.ToString());
                    }
                    else
                    {
                        ctx.Log.Info(message.ToString());
                    }
                }
            }
        }

        private async Task<bool> HasPermission()
        {
            var customer = _services.WorkContext.CurrentCustomer;
            if (customer.SystemName == SystemCustomerNames.BackgroundTask)
            {
                return true;
            }

            return await _services.Permissions.AuthorizeAsync(Permissions.Configuration.Import.Execute);
        }

        #endregion
    }
}