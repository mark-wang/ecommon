using System;
using System.Collections.Concurrent;
using System.Linq;
using ECommon.Components;
using ECommon.Logging;
using ECommon.TcpTransport.Utils;

namespace ECommon.TcpTransport
{
    public class TcpConnectionMonitor
    {
        public static readonly TcpConnectionMonitor Default = new TcpConnectionMonitor();
        private static readonly ILogger _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(typeof(TcpConnectionMonitor).FullName);

        private readonly object _statsLock = new object();

        private readonly ConcurrentDictionary<IMonitoredTcpConnection, ConnectionData> _connections = new ConcurrentDictionary<IMonitoredTcpConnection, ConnectionData>();

        private long _sentTotal;
        private long _receivedTotal;
        private long _sentSinceLastRun;
        private long _receivedSinceLastRun;
        private long _pendingSendOnLastRun;
        private long _inSendOnLastRun;
        private long _pendingReceivedOnLastRun;

        private bool _anySendBlockedOnLastRun;
        private DateTime _lastUpdateTime;

        private TcpConnectionMonitor()
        {
        }

        public void Register(IMonitoredTcpConnection connection)
        {
            _connections.TryAdd(connection, new ConnectionData(connection));
        }

        public void Unregister(IMonitoredTcpConnection connection)
        {
            ConnectionData data;
            _connections.TryRemove(connection, out data);
        }

        public TcpStats GetTcpStats()
        {
            ConnectionData[] connections = _connections.Values.ToArray();
            lock (_statsLock)
            {
                var stats = AnalyzeConnections(connections, DateTime.UtcNow - _lastUpdateTime);
                _lastUpdateTime = DateTime.UtcNow;
                return stats;
            }
        }

        private TcpStats AnalyzeConnections(ConnectionData[] connections, TimeSpan measurePeriod)
        {
            _receivedSinceLastRun = 0;
            _sentSinceLastRun = 0;
            _pendingSendOnLastRun = 0;
            _inSendOnLastRun = 0;
            _pendingReceivedOnLastRun = 0;
            _anySendBlockedOnLastRun = false;

            foreach (var connection in connections)
            {
                AnalyzeConnection(connection);
            }

            var stats = new TcpStats(connections.Length,
                                     _sentTotal,
                                     _receivedTotal,
                                     _sentSinceLastRun,
                                     _receivedSinceLastRun,
                                     _pendingSendOnLastRun,
                                     _inSendOnLastRun,
                                     _pendingReceivedOnLastRun,
                                     measurePeriod);


            if (Application.IsDefined(Application.DumpStatistics))
            {
                _logger.DebugFormat("\n# Total connections: {0,3}. Out: {1:0.00}b/s  In: {2:0.00}b/s  Pending Send: {3}  " +
                          "In Send: {4}  Pending Received: {5} Measure Time: {6}",
                          stats.Connections,
                          stats.SendingSpeed,
                          stats.ReceivingSpeed,
                          stats.PendingSend,
                          stats.InSend,
                          stats.PendingSend,
                          stats.MeasureTime);
            }
            return stats;
        }

        private void AnalyzeConnection(ConnectionData connectionData)
        {
            var connection = connectionData.Connection;
            if (!connection.IsInitialized)
                return;

            if (connection.IsFaulted)
            {
                _logger.FatalFormat("# {0} is faulted", connection);
                return;
            }

            UpdateStatistics(connectionData);

            CheckPendingReceived(connection);
            CheckPendingSend(connection);
            CheckMissingSendCallback(connectionData, connection);
            CheckMissingReceiveCallback(connectionData, connection);
        }

        private void UpdateStatistics(ConnectionData connectionData)
        {
            var connection = connectionData.Connection;
            long totalBytesSent = connection.TotalBytesSent;
            long totalBytesReceived = connection.TotalBytesReceived;
            long pendingSend = connection.PendingSendBytes;
            long inSend = connection.InSendBytes;
            long pendingReceived = connection.PendingReceivedBytes;

            _sentSinceLastRun += totalBytesSent - connectionData.LastTotalBytesSent;
            _receivedSinceLastRun += totalBytesReceived - connectionData.LastTotalBytesReceived;

            _sentTotal += _sentSinceLastRun;
            _receivedTotal += _receivedSinceLastRun;

            _pendingSendOnLastRun += pendingSend;
            _inSendOnLastRun += inSend;
            _pendingReceivedOnLastRun = pendingReceived;

            connectionData.LastTotalBytesSent = totalBytesSent;
            connectionData.LastTotalBytesReceived = totalBytesReceived;
        }

        private static void CheckMissingReceiveCallback(ConnectionData connectionData, IMonitoredTcpConnection connection)
        {
            bool inReceive = connection.InReceive;
            bool isReadyForReceive = connection.IsReadyForReceive;
            DateTime? lastReceiveStarted = connection.LastReceiveStarted;

            int sinceLastReceive = (int)(DateTime.UtcNow - lastReceiveStarted.GetValueOrDefault()).TotalMilliseconds;
            bool missingReceiveCallback = inReceive && isReadyForReceive && sinceLastReceive > 500;

            if (missingReceiveCallback && connectionData.LastMissingReceiveCallBack)
            {
                _logger.ErrorFormat("# {0} {1}ms since last Receive started. No completion callback received, but socket status is READY_FOR_RECEIVE",
                          connection, sinceLastReceive);
            }
            connectionData.LastMissingReceiveCallBack = missingReceiveCallback;
        }

        private void CheckMissingSendCallback(ConnectionData connectionData, IMonitoredTcpConnection connection)
        {
            // snapshot all data?
            bool inSend = connection.InSend;
            bool isReadyForSend = connection.IsReadyForSend;
            DateTime? lastSendStarted = connection.LastSendStarted;
            int inSendBytes = connection.InSendBytes;

            int sinceLastSend = (int)(DateTime.UtcNow - lastSendStarted.GetValueOrDefault()).TotalMilliseconds;
            bool missingSendCallback = inSend && isReadyForSend && sinceLastSend > 500;

            if (missingSendCallback && connectionData.LastMissingSendCallBack)
            {
                // _anySendBlockedOnLastRun = true;
                _logger.ErrorFormat(
                    "# {0} {1}ms since last send started. No completion callback received, but socket status is READY_FOR_SEND. In send: {2}",
                    connection, sinceLastSend, inSendBytes);
            }
            connectionData.LastMissingSendCallBack = missingSendCallback;
        }

        private static void CheckPendingSend(IMonitoredTcpConnection connection)
        {
            int pendingSendBytes = connection.PendingSendBytes;
            if (pendingSendBytes > 128 * 1024)
            {
                _logger.InfoFormat("# {0} {1}kb pending send", connection, pendingSendBytes / 1024);
            }
        }

        private static void CheckPendingReceived(IMonitoredTcpConnection connection)
        {
            int pendingReceivedBytes = connection.PendingReceivedBytes;
            if (pendingReceivedBytes > 128 * 1024)
            {
                _logger.InfoFormat("# {0} {1}kb are not dispatched", connection, pendingReceivedBytes / 1024);
            }
        }

        public bool IsSendBlocked()
        {
            return _anySendBlockedOnLastRun;
        }

        private class ConnectionData
        {
            public readonly IMonitoredTcpConnection Connection;
            public bool LastMissingSendCallBack;
            public bool LastMissingReceiveCallBack;
            public long LastTotalBytesSent;
            public long LastTotalBytesReceived;

            public ConnectionData(IMonitoredTcpConnection connection)
            {
                Connection = connection;
            }
        }
    }
}