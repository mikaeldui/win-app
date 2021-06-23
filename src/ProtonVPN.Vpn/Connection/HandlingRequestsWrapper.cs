﻿/*
 * Copyright (c) 2020 Proton Technologies AG
 *
 * This file is part of ProtonVPN.
 *
 * ProtonVPN is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * ProtonVPN is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using ProtonVPN.Common;
using ProtonVPN.Common.Logging;
using ProtonVPN.Common.Threading;
using ProtonVPN.Common.Vpn;
using ProtonVPN.Vpn.Common;

namespace ProtonVPN.Vpn.Connection
{
    /// <summary>
    /// Handles <see cref="Connect"/> and <see cref="Disconnect"/> requests by disconnecting
    /// first if not disconnected yet and connecting only when disconnect completes.
    /// A wrapper around <see cref="ISingleVpnConnection"/>.
    /// </summary>
    /// <remarks>
    /// On connect request sends queued <see cref="StateChanged"/> event containing status value <see cref="VpnStatus.Connecting"/>.
    /// </remarks>
    internal class HandlingRequestsWrapper : ISingleVpnConnection
    {
        private readonly ISingleVpnConnection _origin;
        private readonly ILogger _logger;
        private readonly ITaskQueue _taskQueue;

        private bool _connectRequested;
        private bool _connecting;
        private bool _disconnectRequested;
        private bool _disconnecting;
        private bool _disconnected;
        private VpnError _disconnectError;
        private VpnEndpoint _endpoint;
        private VpnCredentials _credentials;
        private VpnConfig _config;

        public HandlingRequestsWrapper(
            ILogger logger,
            ITaskQueue taskQueue,
            ISingleVpnConnection origin)
        {
            _logger = logger;
            _taskQueue = taskQueue;
            _origin = origin;

            _origin.StateChanged += Origin_StateChanged;
        }

        public event EventHandler<EventArgs<VpnState>> StateChanged;

        public InOutBytes Total => _origin.Total;

        public void Connect(VpnEndpoint endpoint, VpnCredentials credentials, VpnConfig config)
        {
            _endpoint = endpoint;
            _credentials = credentials;
            _config = config;

            _connectRequested = true;
            _disconnectRequested = false;
            _disconnectError = VpnError.Unknown;

            _logger.Info("HandlingRequestsWrapper: Connect requested, queuing Connect");
            Queued(Connect);
        }

        public void Disconnect(VpnError error)
        {
            _connectRequested = false;
            _disconnectRequested = true;
            _disconnectError = error;

            _logger.Info("HandlingRequestsWrapper: Disconnect requested, queuing Disconnect");
            Queued(Disconnect);
        }

        public void PassThroughDisconnectIfNotDisconnected(VpnError error)
        {
            if (!_disconnected)
            {
                _logger.Info("HandlingRequestsWrapper: Disconnect pass-through requested, queuing pass-through disconnect");
                _connectRequested = false;
                _disconnectRequested = false;
                _disconnecting = false;
                _connecting = false;
                Queued(() => _origin.Disconnect(error));
            }
        }

        private void Origin_StateChanged(object sender, EventArgs<VpnState> e)
        {
            OnStateChanged(e.Data);
        }

        private void OnStateChanged(VpnState state)
        {
            _disconnected = state.Status == VpnStatus.Disconnected;

            if (_connectRequested)
            {
                InvokeConnecting();

                if (_disconnected)
                {
                    _logger.Info("HandlingRequestsWrapper: Already disconnected, queuing Connect");
                    Queued(Connect);
                }

                return;
            }

            if (_disconnectRequested || _disconnecting)
            {
                if (state.Status == VpnStatus.Disconnecting || state.Status == VpnStatus.Disconnected)
                {
                    InvokeStateChanged(state.WithError(_disconnectError));
                    return;
                }

                InvokeDisconnecting();

                return;
            }

            if (_connecting && state.Status == VpnStatus.Disconnecting)
            {
                // Force disconnect if disconnected while connecting
                _disconnectRequested = true;
                _disconnectError = state.Error;
                _logger.Info("HandlingRequestsWrapper: Disconnecting unexpectedly, queuing Disconnect");
                Queued(Disconnect);
            }

            if (state.Status == VpnStatus.Disconnecting || state.Status == VpnStatus.Disconnected)
            {
                var error = state.Error;

                if (_connecting)
                {
                    // Force disconnect if disconnected while connecting
                    _disconnectRequested = true;
                    _disconnectError = error;
                    _logger.Info("HandlingRequestsWrapper: Disconnecting unexpectedly, queuing Disconnect");
                    Queued(Disconnect);
                    return;
                }

                InvokeStateChanged(state.WithError(error));
                return;
            }

            InvokeStateChanged(WithFallbackRemoteServer(state, _endpoint.Server));
        }

        private void Connect()
        {
            if (_connectRequested)
            {
                _disconnecting = false;
                InvokeConnecting();

                if (_disconnected)
                {
                    _connectRequested = false;
                    _connecting = true;
                    _logger.Info("HandlingRequestsWrapper: Connecting");
                    _origin.Connect(_endpoint, _credentials, _config);
                }
                else
                {
                    _connecting = false;
                    _logger.Info("HandlingRequestsWrapper: Not yet disconnected, Disconnecting");
                    _origin.Disconnect(VpnError.None);
                }
            }
        }

        private void Disconnect()
        {
            if (_disconnectRequested)
            {
                _disconnecting = true;

                if (_disconnected)
                {
                    InvokeDisconnected();
                }
                else
                {
                    InvokeDisconnecting();

                    _connecting = false;
                    _logger.Info("HandlingRequestsWrapper: Disconnecting");
                    _origin.Disconnect(_disconnectError);
                }

                _disconnectRequested = false;
            }
        }

        private void InvokeConnecting()
        {
            InvokeStateChanged(new VpnState(
                VpnStatus.Connecting,
                VpnError.None,
                string.Empty,
                _endpoint.Server.Ip, 
                label: _endpoint.Server.Label));
        }

        private void InvokeDisconnecting()
        {
            InvokeStateChanged(new VpnState(VpnStatus.Disconnecting, _disconnectError));
        }

        private void InvokeDisconnected()
        {
            InvokeStateChanged(new VpnState(VpnStatus.Disconnected, _disconnectError));
        }

        private void InvokeStateChanged(VpnState state)
        {
            StateChanged?.Invoke(this, new EventArgs<VpnState>(state));
        }

        private VpnState WithFallbackRemoteServer(VpnState state, VpnHost remoteServer)
        {
            if (state.Status == VpnStatus.Disconnecting || 
                state.Status == VpnStatus.Disconnected ||
                !string.IsNullOrEmpty(state.RemoteIp))
            {
                return state;
            }

            return state.WithRemoteIp(remoteServer.Ip, remoteServer.Label);
        }

        private void Queued(Action action)
        {
            _taskQueue.Enqueue(action);
        }
    }
}
