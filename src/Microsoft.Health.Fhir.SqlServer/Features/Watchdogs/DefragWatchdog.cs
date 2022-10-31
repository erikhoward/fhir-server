﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Messages.Storage;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.Fhir.SqlServer.Features.Schema.Model;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.JobManagement;
using Microsoft.Health.SqlServer.Features.Client;
using Microsoft.Health.SqlServer.Features.Schema;

namespace Microsoft.Health.Fhir.SqlServer.Features.Watchdogs
{
    public sealed class DefragWatchdog : INotificationHandler<StorageInitializedNotification>
    {
        private const byte QueueType = (byte)Core.Features.Operations.QueueType.Defrag;
        private int _threads;
        private int _heartbeatPeriodSec;
        private int _heartbeatTimeoutSec;
        private double _periodSec;

        private readonly Func<IScoped<SqlConnectionWrapperFactory>> _sqlConnectionWrapperFactory;
        private readonly SchemaInformation _schemaInformation;
        private readonly Func<IScoped<SqlQueueClient>> _sqlQueueClient;
        private readonly ILogger<DefragWatchdog> _logger;

        private TimeSpan _timerDelay;
        private bool _storageReady;

        public DefragWatchdog(
            Func<IScoped<SqlConnectionWrapperFactory>> sqlConnectionWrapperFactory,
            SchemaInformation schemaInformation,
            Func<IScoped<SqlQueueClient>> sqlQueueClient,
            ILogger<DefragWatchdog> logger)
        {
            _schemaInformation = EnsureArg.IsNotNull(schemaInformation, nameof(schemaInformation));
            _sqlConnectionWrapperFactory = EnsureArg.IsNotNull(sqlConnectionWrapperFactory, nameof(sqlConnectionWrapperFactory));
            _sqlQueueClient = EnsureArg.IsNotNull(sqlQueueClient, nameof(sqlQueueClient));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        internal DefragWatchdog()
        {
            // this is used to get param names for testing
        }

        internal string Name => GetType().Name;

        internal string IsEnabledId => $"{Name}.IsEnabled";

        internal string PeriodSecId => $"{Name}.PeriodSec";

        internal string HeartbeatPeriodSecId => $"{Name}.HeartbeatPeriodSec";

        internal string HeartbeatTimeoutSecId => $"{Name}.HeartbeatTimeoutSec";

        internal string ThreadsId => $"{Name}.Threads";

        public async Task Initialize(CancellationToken cancellationToken)
        {
            // wait till we can truly init
            while (!_storageReady || _schemaInformation.Current < SchemaVersionConstants.Defrag)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }

            await InitParamsAsync();

            _timerDelay = TimeSpan.FromSeconds(_periodSec);
        }

        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            bool initialRun = true;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!initialRun)
                {
                    await Task.Delay(_timerDelay, cancellationToken);
                }
                else
                {
                    await Task.Delay(RandomDelay(), cancellationToken);
                }

                initialRun = false;

                if (cancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                if (!await IsEnabled(cancellationToken))
                {
                    _logger.LogInformation("DefragWatchdog is disabled.");
                    continue;
                }

                try
                {
                    var job = await GetCoordinatorJob(cancellationToken);

                    if (job.jobId == -1)
                    {
                        _logger.LogInformation("Coordinator job was not found.");
                        continue;
                    }

                    _logger.LogInformation("DefragWatchdog found JobId: {JobId}, {ActiveDefragItems} active defrag items, executing.", job.jobId, job.activeDefragItems);

                    await ExecWithHeartbeatAsync(
                        async cancellationSource =>
                        {
                            try
                            {
                                var newDefragItems = await InitDefragAsync(job.groupId, cancellationSource);
                                _logger.LogInformation("{NewDefragItems} new defrag items found for Group: {GroupId}.", newDefragItems, job.groupId);
                                if (job.activeDefragItems > 0 || newDefragItems > 0)
                                {
                                    await ChangeDatabaseSettings(false, cancellationSource);

                                    var tasks = new List<Task>();
                                    for (var thread = 0; thread < _threads; thread++)
                                    {
                                        tasks.Add(ExecDefrag(cancellationSource));
                                    }

                                    await Task.WhenAll(tasks);
                                    await ChangeDatabaseSettings(true, cancellationSource);

                                    _logger.LogInformation("All {ParallelTasks} tasks complete for Group: {GroupId}.", tasks.Count, job.groupId);
                                    _logger.LogInformation("Group={GroupId} Job={JobId}: All ParallelTasks={ParallelTasks} tasks completed.", job.groupId, job.jobId, tasks.Count);
                                }
                                else
                                {
                                    _logger.LogInformation("No defrag items found for Group: {GroupId}.", job.groupId);
                                }
                            }
                            catch (Exception e)
                            {
                                _logger.LogError(e, "Defrag failed.");
                                throw;
                            }
                        },
                        job.jobId,
                        job.version,
                        cancellationToken);

                    await CompleteJob(job.jobId, job.version, false, cancellationToken);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "DefragWatchdog failed.");
                }
            }

            _logger.LogInformation("DefragWatchdog stopped.");
        }

        private async Task ChangeDatabaseSettings(bool isOn, CancellationToken cancellationToken)
        {
            using IScoped<SqlConnectionWrapperFactory> scopedSqlConnectionWrapperFactory = _sqlConnectionWrapperFactory.Invoke();
            using SqlConnectionWrapper conn = await scopedSqlConnectionWrapperFactory.Value.ObtainSqlConnectionWrapperAsync(cancellationToken, enlistInTransaction: false);
            using SqlCommandWrapper cmd = conn.CreateRetrySqlCommand();
            VLatest.DefragChangeDatabaseSettings.PopulateCommand(cmd, isOn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation("ChangeDatabaseSettings: {IsOn}.", isOn);
        }

        private async Task ExecDefrag(CancellationToken cancellationToken)
        {
            while (true)
            {
                (long groupId, long jobId, long version, string definition) job = await DequeueJobAsync(jobId: null, cancellationToken);

                long jobId = job.jobId;

                if (jobId == -1)
                {
                    return;
                }

                await ExecWithHeartbeatAsync(
                    async cancellationSource =>
                    {
                        var split = job.definition.Split(";");
                        await DefragAsync(split[0], split[1], int.Parse(split[2]), byte.Parse(split[3]) == 1, cancellationSource);
                    },
                    jobId,
                    job.version,
                    cancellationToken);

                await CompleteJob(jobId, job.version, false, cancellationToken);
            }
        }

        private async Task DefragAsync(string table, string index, int partitionNumber, bool isPartitioned, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(table, nameof(table));
            EnsureArg.IsNotNullOrWhiteSpace(index, nameof(index));

            _logger.LogInformation("Beginning defrag on Table: {Table}, Index: {Index}, Partition: {PartitionNumber}", table, index, partitionNumber);

            using IScoped<SqlConnectionWrapperFactory> scopedSqlConnectionWrapperFactory = _sqlConnectionWrapperFactory.Invoke();
            using SqlConnectionWrapper conn = await scopedSqlConnectionWrapperFactory.Value.ObtainSqlConnectionWrapperAsync(cancellationToken, enlistInTransaction: false);
            using SqlCommandWrapper cmd = conn.CreateRetrySqlCommand();
            VLatest.Defrag.PopulateCommand(cmd, table, index, partitionNumber, isPartitioned);
            cmd.CommandTimeout = 0; // this is long running
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation("Finished defrag on Table: {Table}, Index: {Index}, Partition: {PartitionNumber}", table, index, partitionNumber);
        }

        private async Task CompleteJob(long jobId, long version, bool failed, CancellationToken cancellationToken)
        {
            using IScoped<SqlQueueClient> scopedQueueClient = _sqlQueueClient.Invoke();
            var jobInfo = new JobInfo { QueueType = QueueType, Id = jobId, Version = version, Status = failed ? JobStatus.Failed : JobStatus.Completed };
            await scopedQueueClient.Value.CompleteJobAsync(jobInfo, false, cancellationToken);

            _logger.LogInformation("Completed JobId: {JobId}, Version: {Version}, Failed: {Failed}", jobId, version, failed);
        }

        private async Task ExecWithHeartbeatAsync(Func<CancellationToken, Task> action, long jobId, long version, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(action, nameof(action));

            using var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_heartbeatPeriodSec));

            CancellationToken timerToken = tokenSource.Token;

            Task heartBeatTask = HeartbeatLoop(jobId, version, timer, timerToken);
            Task<Task> actionTask = action.Invoke(timerToken).ContinueWith(
                _ =>
                {
                    tokenSource.Cancel();
                    return Task.CompletedTask;
                },
                TaskScheduler.Current);

            try
            {
                await Task.WhenAll(actionTask, heartBeatTask);
            }
            catch (OperationCanceledException) when (tokenSource.IsCancellationRequested)
            {
                // ending heartbeat loop
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExecWithHeartbeatAsync failed.");
                throw;
            }

            if (!actionTask.IsCompleted)
            {
                await actionTask;
            }
        }

        private async Task HeartbeatLoop(long jobId, long version, PeriodicTimer timer, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(timer, nameof(timer));

            while (!cancellationToken.IsCancellationRequested)
            {
                await timer.WaitForNextTickAsync(cancellationToken);
                await UpdateJobHeartbeatAsync(jobId, version, cancellationToken);
            }
        }

        private async Task UpdateJobHeartbeatAsync(long jobId, long version, CancellationToken cancellationToken)
        {
            using IScoped<SqlQueueClient> scopedQueueClient = _sqlQueueClient.Invoke();
            var jobInfo = new JobInfo { QueueType = QueueType, Id = jobId, Version = version };
            await scopedQueueClient.Value.KeepAliveJobAsync(jobInfo, cancellationToken);
        }

        private async Task<int?> InitDefragAsync(long groupId, CancellationToken cancellationToken)
        {
            using IScoped<SqlConnectionWrapperFactory> scopedSqlConnectionWrapperFactory = _sqlConnectionWrapperFactory.Invoke();
            using SqlConnectionWrapper conn = await scopedSqlConnectionWrapperFactory.Value.ObtainSqlConnectionWrapperAsync(cancellationToken, enlistInTransaction: false);
            using SqlCommandWrapper cmd = conn.CreateRetrySqlCommand();
            var defragItems = 0;
            VLatest.InitDefrag.PopulateCommand(cmd, QueueType, groupId, defragItems);
            cmd.CommandTimeout = 0; // this is long running
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return VLatest.InitDefrag.GetOutputs(cmd);
        }

        private async Task<(long groupId, long jobId, long version, int activeDefragItems)> GetCoordinatorJob(CancellationToken cancellationToken)
        {
            var activeDefragItems = 0;
            using IScoped<SqlQueueClient> scopedQueueClient = _sqlQueueClient.Invoke();
            var queueClient = scopedQueueClient.Value;
            await queueClient.ArchiveJobsAsync(QueueType, cancellationToken);

            (long groupId, long jobId, long version) id = (-1, -1, -1);
            try
            {
                JobInfo job = (await queueClient.EnqueueAsync(QueueType, new[] { "Defrag" }, null, true, false, cancellationToken)).FirstOrDefault();

                if (job != null)
                {
                    id = (job.GroupId, job.Id, job.Version);
                }
            }
            catch (Exception e)
            {
                if (!e.Message.Contains("There are other active job groups", StringComparison.OrdinalIgnoreCase))
                {
                    throw;
                }
            }

            if (id.jobId == -1)
            {
                var active = await GetActiveCoordinatorJobAsync(cancellationToken);
                id = (active.groupId, active.jobId, active.version);
                activeDefragItems = active.activeDefragItems;
            }

            if (id.jobId != -1)
            {
                var job = await DequeueJobAsync(id.jobId, cancellationToken);
                id = (job.groupId, job.jobId, job.version);
            }

            return (id.groupId, id.jobId, id.version, activeDefragItems);
        }

        private async Task<(long groupId, long jobId, long version, string definition)> DequeueJobAsync(long? jobId = null, CancellationToken cancellationToken = default)
        {
            using IScoped<SqlQueueClient> scopedQueueClient = _sqlQueueClient.Invoke();
            JobInfo job = await scopedQueueClient.Value.DequeueAsync(QueueType, Environment.MachineName, _heartbeatTimeoutSec, cancellationToken, jobId);

            if (job != null)
            {
                return (job.GroupId, job.Id, job.Version, job.Definition);
            }

            return (-1, -1, -1, string.Empty);
        }

        private async Task<(long groupId, long jobId, long version, int activeDefragItems)> GetActiveCoordinatorJobAsync(CancellationToken cancellationToken)
        {
            using IScoped<SqlConnectionWrapperFactory> scopedSqlConnectionWrapperFactory = _sqlConnectionWrapperFactory.Invoke();
            using SqlConnectionWrapper conn = await scopedSqlConnectionWrapperFactory.Value.ObtainSqlConnectionWrapperAsync(cancellationToken, enlistInTransaction: false);
            using SqlCommandWrapper cmd = conn.CreateRetrySqlCommand();

            // cannot use VLatest as it incorrectly asks for optional group id
            cmd.CommandText = "dbo.GetActiveJobs";
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.Parameters.AddWithValue("@QueueType", QueueType);

            (long groupId, long jobId, long version) id = (-1, -1, -1);
            var activeDefragItems = 0;
            await using SqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(2) == "Defrag")
                {
                    id = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(3));
                }
                else
                {
                    activeDefragItems++;
                }
            }

            return (id.groupId, id.jobId, id.version, activeDefragItems);
        }

        private async Task<double> GetNumberParameterById(string id, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrEmpty(id, nameof(id));

            using IScoped<SqlConnectionWrapperFactory> scopedSqlConnectionWrapperFactory = _sqlConnectionWrapperFactory.Invoke();
            using SqlConnectionWrapper conn = await scopedSqlConnectionWrapperFactory.Value.ObtainSqlConnectionWrapperAsync(cancellationToken, enlistInTransaction: false);
            using SqlCommandWrapper cmd = conn.CreateRetrySqlCommand();

            cmd.CommandText = "SELECT Number FROM dbo.Parameters WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Id", id);
            var value = await cmd.ExecuteScalarAsync(cancellationToken);

            if (value == null)
            {
                throw new InvalidOperationException($"{id} is not set correctly in the Parameters table.");
            }

            return (double)value;
        }

        private async Task<int> GetThreads(CancellationToken cancellationToken)
        {
            var value = await GetNumberParameterById(ThreadsId, cancellationToken);
            return (int)value;
        }

        private async Task<int> GetHeartbeatPeriod(CancellationToken cancellationToken)
        {
            var value = await GetNumberParameterById(HeartbeatPeriodSecId, cancellationToken);
            return (int)value;
        }

        private async Task<int> GetHeartbeatTimeout(CancellationToken cancellationToken)
        {
            var value = await GetNumberParameterById(HeartbeatTimeoutSecId, cancellationToken);
            return (int)value;
        }

        private async Task<double> GetPeriod(CancellationToken cancellationToken)
        {
            var value = await GetNumberParameterById(PeriodSecId, cancellationToken);
            return value;
        }

        private async Task<bool> IsEnabled(CancellationToken cancellationToken)
        {
            var value = await GetNumberParameterById(IsEnabledId, cancellationToken);
            return value == 1;
        }

        private async Task InitParamsAsync()
        {
            // No CancellationToken is passed since we shouldn't cancel initialization.

            _logger.LogInformation("InitParams starting...");

            // Offset for other instances running init
            await Task.Delay(RandomDelay(), CancellationToken.None);

            using IScoped<SqlConnectionWrapperFactory> scopedConn = _sqlConnectionWrapperFactory.Invoke();
            using SqlConnectionWrapper conn = await scopedConn.Value.ObtainSqlConnectionWrapperAsync(CancellationToken.None, false);
            using SqlCommandWrapper cmd = conn.CreateRetrySqlCommand();

            cmd.CommandText = @"
INSERT INTO dbo.Parameters (Id,Number) SELECT @IsEnabledId, 0
INSERT INTO dbo.Parameters (Id,Number) SELECT @ThreadsId, 4
INSERT INTO dbo.Parameters (Id,Number) SELECT @PeriodSecId, 24*3600
INSERT INTO dbo.Parameters (Id,Number) SELECT @HeartbeatPeriodSecId, 60
INSERT INTO dbo.Parameters (Id,Number) SELECT @HeartbeatTimeoutSecId, 600
INSERT INTO dbo.Parameters (Id,Char) SELECT name, 'LogEvent' FROM sys.objects WHERE type = 'p' AND name LIKE '%defrag%'
            ";

            cmd.Parameters.AddWithValue("@IsEnabledId", IsEnabledId);
            cmd.Parameters.AddWithValue("@ThreadsId", ThreadsId);
            cmd.Parameters.AddWithValue("@PeriodSecId", PeriodSecId);
            cmd.Parameters.AddWithValue("@HeartbeatPeriodSecId", HeartbeatPeriodSecId);
            cmd.Parameters.AddWithValue("@HeartbeatTimeoutSecId", HeartbeatTimeoutSecId);

            await cmd.ExecuteNonQueryAsync(CancellationToken.None);

            _threads = await GetThreads(CancellationToken.None);
            _heartbeatPeriodSec = await GetHeartbeatPeriod(CancellationToken.None);
            _heartbeatTimeoutSec = await GetHeartbeatTimeout(CancellationToken.None);
            _periodSec = await GetPeriod(CancellationToken.None);

            _logger.LogInformation("InitParams completed.");
        }

        private static TimeSpan RandomDelay()
        {
            return TimeSpan.FromSeconds(RandomNumberGenerator.GetInt32(10) / 10.0);
        }

        public Task Handle(StorageInitializedNotification notification, CancellationToken cancellationToken)
        {
            _storageReady = true;
            return Task.CompletedTask;
        }
    }
}