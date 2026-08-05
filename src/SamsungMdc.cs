// For Basic SIMPL# Classes
// For Basic SIMPL#Pro classes

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro.DeviceSupport;
using Newtonsoft.Json;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.DeviceInfo;
using PepperDash.Essentials.Core.DeviceTypeInterfaces;
using PepperDash.Essentials.Devices.Displays;
using Feedback = PepperDash.Essentials.Core.Feedback;
using GenericTcpIpClient = PepperDash.Core.GenericTcpIpClient;

namespace PepperDashPluginSamsungMdcDisplay
{
    public class SamsungMdcDisplayController
        : PepperDash.Essentials.Devices.Common.Displays.TwoWayDisplayBase,
            IBasicVolumeWithFeedback,
            ICommunicationMonitor,
            IBridgeAdvanced,
            IDeviceInfoProvider,
            IInputDisplayPort1,
            IInputDisplayPort2,
            IInputHdmi1,
            IInputHdmi2,
            IInputHdmi3,
            IInputHdmi4,
            IHasInputs<byte>,
            IRoutingSinkWithCurrentSources
    {
        public StatusMonitorBase CommunicationMonitor { get; private set; }
        public IBasicCommunication Communication { get; private set; }
        private byte[] _incomingBuffer = { };

        public IntFeedback StatusFeedback { get; set; }

        private readonly SamsungMdcDisplayPropertiesConfig _config;
        public byte Id { get; private set; }
        private readonly uint _coolingTimeMs;
        private readonly uint _warmingTimeMs;
        private readonly long _pollIntervalMs;
        private readonly List<CustomInput> _customInputs;

        private CTimer _pollTimer;

        private bool _isPoweringOnIgnorePowerFb;

        private bool _powerIsOn;
        protected override Func<bool> PowerIsOnFeedbackFunc
        {
            get { return () => _powerIsOn; }
        }

        private bool _isCoolingDown;
        protected override Func<bool> IsCoolingDownFeedbackFunc
        {
            get { return () => _isCoolingDown; }
        }

        private bool _isWarmingUp;
        protected override Func<bool> IsWarmingUpFeedbackFunc
        {
            get { return () => _isWarmingUp; }
        }

        public static List<string> InputKeys = new List<string>();

        public List<BoolFeedback> InputFeedback;
        public IntFeedback InputNumberFeedback;

        protected override Func<string> CurrentInputFeedbackFunc
        {
            get { return () => CurrentInputPort != null ? CurrentInputPort.Key : string.Empty; }
        }

        private int _currentInputNumber;
        public int CurrentInputNumber
        {
            get { return _currentInputNumber; }
            private set
            {
                _currentInputNumber = value;
                this.LogDebug(
                    "CurrentInputNumber: _currentInputNumber-'{currentInput}'",
                    _currentInputNumber
                );

                CurrentInputFeedback.FireUpdate();
                InputNumberFeedback.FireUpdate();

                UpdateBooleanFeedback();
            }
        }
        public ISelectableItems<byte> Inputs { get; private set; }

        public void SetInput(int value)
        {
            if (value <= 0 || value >= InputPorts.Count)
                return;

            // -1 to get actual input after'0d' check
            var port = GetInputPort(value - 1);
            if (port == null)
            {
                this.LogDebug("SetInput: failed to get input port");
                return;
            }

            this.LogDebug(
                "SetInput: port.Key-'{0}', port.Selector-'{1}', port.ConnectionType-'{2}', port.FeedbackMatchObject-'{3}'",
                port.Key,
                port.Selector,
                port.ConnectionType,
                port.FeedbackMatchObject
            );

            ExecuteSwitch(port.Selector);
            UpdateInputFb((byte)port.FeedbackMatchObject);
        }

        private RoutingInputPort GetInputPort(int input)
        {
            return InputPorts.ElementAt(input);
        }

        public const int InputPowerOn = 101;
        public const int InputPowerOff = 102;

        private readonly int _lowerLimit;
        private readonly int _upperLimit;
        private bool ScaleVolume { get; set; }

        private bool _lastCommandSentWasVolume;
        private int _lastVolumeSent;
        private bool _volumeIsRamping;
        private ushort _volumeLevelForSig;
        private byte _lastVolumeFeedbackReceived;
        private byte _lastMuteFeedbackReceived;
        private byte _lastInputFeedbackReceived;
        private bool _isMuted;

        private CTimer _volumeDebounceTimer;
        private ushort _queuedVolumeLevel;

        private readonly bool _showVolumeControls;
        private ActionIncrementer _volumeIncrementer;

        public IntFeedback CurrentLedTemperatureCelsiusFeedback;
        public IntFeedback CurrentLedTemperatureFahrenheitFeedback;

        private readonly bool _pollLedTemps;
        private int _currentLedTemperatureCelsius;
        public int CurrentLedTemperatureCelsius
        {
            get { return _currentLedTemperatureCelsius; }
            set
            {
                _currentLedTemperatureCelsius = value;
                CurrentLedTemperatureCelsiusFeedback.FireUpdate();
            }
        }
        private int _currentLedTemperatureFahrenheit;
        public int CurrentLedTemperatureFahrenheit
        {
            get { return _currentLedTemperatureFahrenheit; }
            set
            {
                _currentLedTemperatureFahrenheit = value;
                CurrentLedTemperatureFahrenheitFeedback.FireUpdate();
            }
        }

        /// <summary>
        /// Constructor for IBaseCommunication
        /// </summary>
        /// <param name="name"></param>
        /// <param name="config"></param>
        /// <param name="key"></param>
        /// <param name="comms"></param>
        public SamsungMdcDisplayController(
            string key,
            string name,
            SamsungMdcDisplayPropertiesConfig config,
            IBasicCommunication comms
        )
            : base(key, name)
        {
            Communication = comms;
            Communication.BytesReceived += Communication_BytesReceived;
            _config = config;

            Id = _config.Id == null ? (byte)0x01 : Convert.ToByte(_config.Id, 16);

            _upperLimit = _config.VolumeUpperLimit;
            _lowerLimit = _config.VolumeLowerLimit;
            _pollIntervalMs = _config.PollIntervalMs;
            _coolingTimeMs = _config.CoolingTimeMs;
            _warmingTimeMs = _config.WarmingTimeMs;
            _showVolumeControls = _config.ShowVolumeControls;
            _pollLedTemps = config.PollLedTemps;
            _customInputs = config.CustomInputs;

            DeviceInfo = new DeviceInfo();

            if (comms is ISocketStatus socket)
            {
                if (comms is GenericTcpIpClient tcpip)
                {
                    DeviceInfo.IpAddress = tcpip.Hostname;
                }
            }

            Init();
        }

        public override FeedbackCollection<Feedback> Feedbacks
        {
            get
            {
                var list = base.Feedbacks;
                list.AddRange(
                    new List<Feedback>
                    {
                        VolumeLevelFeedback,
                        MuteFeedback,
                        CurrentInputFeedback,
                        CurrentLedTemperatureCelsiusFeedback,
                        CurrentLedTemperatureFahrenheitFeedback,
                    }
                );
                return list;
            }
        }

        #region IBridgeAdvanced Members

        /// <summary>
        /// LinkToApi (bridge method)
        /// </summary>
        /// <param name="trilist"></param>
        /// <param name="joinStart"></param>
        /// <param name="joinMapKey"></param>
        /// <param name="bridge"></param>
        public void LinkToApi(
            BasicTriList trilist,
            uint joinStart,
            string joinMapKey,
            EiscApiAdvanced bridge
        )
        {
            var joinMap = new SamsungDisplayControllerJoinMap(joinStart);

            if (bridge != null)
                bridge.AddJoinMap(Key, joinMap);

            var joinMapSerialized = JoinMapHelper.GetSerializedJoinMapForDevice(joinMapKey);

            if (!string.IsNullOrEmpty(joinMapSerialized))
            {
                joinMap = JsonConvert.DeserializeObject<SamsungDisplayControllerJoinMap>(
                    joinMapSerialized
                );
            }

            this.LogDebug("Linking to Trilist '{0}'", trilist.ID.ToString("X"));
            this.LogVerbose("Linking to Display: {0}", Name);

            //trilist.StringInput[joinMap.Name.JoinNumber].StringValue = Name;
            trilist.SetString(joinMap.Name.JoinNumber, Name);

            CommunicationMonitor.IsOnlineFeedback.LinkInputSig(
                trilist.BooleanInput[joinMap.IsOnline.JoinNumber]
            );
            StatusFeedback.LinkInputSig(trilist.UShortInput[joinMap.Status.JoinNumber]);

            // Power Off
            trilist.SetSigTrueAction(joinMap.PowerOff.JoinNumber, PowerOff);
            PowerIsOnFeedback.LinkComplementInputSig(
                trilist.BooleanInput[joinMap.PowerOff.JoinNumber]
            );

            // Power On
            trilist.SetSigTrueAction(joinMap.PowerOn.JoinNumber, PowerOn);
            PowerIsOnFeedback.LinkInputSig(trilist.BooleanInput[joinMap.PowerOn.JoinNumber]);

            // Input digitals
            var count = 0;
            foreach (var input in InputPorts)
            {
                var i = input;
                var inputIndex = count + 1;
                trilist.SetSigTrueAction(
                    (ushort)(joinMap.InputSelectOffset.JoinNumber + count),
                    () =>
                    {
                        this.LogVerbose("InputSelect Digital-'{0}'", inputIndex);
                        SetInput(inputIndex);
                    }
                );

                trilist
                    .StringInput[(ushort)(joinMap.InputNamesOffset.JoinNumber + count)]
                    .StringValue = i.Key;

                InputFeedback[count]
                    .LinkInputSig(
                        trilist.BooleanInput[joinMap.InputSelectOffset.JoinNumber + (uint)count]
                    );
                count++;
            }

            // Input Analog
            trilist.SetUShortSigAction(
                joinMap.InputSelect.JoinNumber,
                a =>
                {
                    this.LogVerbose("InputSelect Analog-'{0}'", a);
                    SetInput(a);
                }
            );

            // input analog feedback
            InputNumberFeedback.LinkInputSig(trilist.UShortInput[joinMap.InputSelect.JoinNumber]);
            InputNumberFeedback.LinkInputSig(trilist.UShortInput[joinMap.InputSelect.JoinNumber]);
            CurrentInputFeedback.OutputChange += (sender, args) =>
                this.LogDebug("CurrentInputFeedback: {0}", args.StringValue);

            // Volume
            trilist.SetBoolSigAction(joinMap.VolumeUp.JoinNumber, VolumeUp);
            trilist.SetBoolSigAction(joinMap.VolumeDown.JoinNumber, VolumeDown);
            trilist.SetSigTrueAction(joinMap.VolumeMute.JoinNumber, MuteToggle);
            trilist.SetSigTrueAction(joinMap.VolumeMuteOn.JoinNumber, MuteOn);
            trilist.SetSigTrueAction(joinMap.VolumeMuteOff.JoinNumber, MuteOff);
            trilist.SetUShortSigAction(joinMap.VolumeLevel.JoinNumber, SetVolume);
            VolumeLevelFeedback.LinkInputSig(trilist.UShortInput[joinMap.VolumeLevel.JoinNumber]);
            MuteFeedback.LinkInputSig(trilist.BooleanInput[joinMap.VolumeMuteOn.JoinNumber]);
            MuteFeedback.LinkInputSig(trilist.BooleanInput[joinMap.VolumeMute.JoinNumber]);
            MuteFeedback.LinkComplementInputSig(
                trilist.BooleanInput[joinMap.VolumeMuteOff.JoinNumber]
            );

            // Show Volume Controls
            trilist.SetBool(joinMap.VolumeControlsVisibleFb.JoinNumber, _showVolumeControls);

            // LED temperature analog feedback
            CurrentLedTemperatureCelsiusFeedback.LinkInputSig(
                trilist.UShortInput[joinMap.LedTemperatureCelsius.JoinNumber]
            );

            CurrentLedTemperatureCelsiusFeedback.LinkInputSig(
                trilist.UShortInput[joinMap.LedTemperatureCelsius.JoinNumber]
            );

            // bridge online change
            trilist.OnlineStatusChange += (sender, args) =>
            {
                if (!args.DeviceOnLine)
                    return;

                trilist.SetString(joinMap.Name.JoinNumber, Name);

                PowerIsOnFeedback.FireUpdate();
                CurrentInputFeedback.FireUpdate();

                for (var port = 0; port < InputPorts.Count; port++)
                {
                    InputFeedback[port].FireUpdate();
                }

                InputNumberFeedback.FireUpdate();

                trilist.SetBool(joinMap.VolumeControlsVisibleFb.JoinNumber, _showVolumeControls);
                VolumeLevelFeedback.FireUpdate();
                MuteFeedback.FireUpdate();

                CurrentLedTemperatureCelsiusFeedback.FireUpdate();
                CurrentLedTemperatureFahrenheitFeedback.FireUpdate();
            };
        }

        #endregion

        public override void Initialize()
        {
            Communication.Connect();

            CommunicationMonitor.Start();
        }

        /// <summary>
        /// Formats an outgoing message.
        /// Third byte will be replaced with ID and last byte will be replaced with calculated checksum.
        /// All bytes to make a valid message must be included and can be represented with 0x00.
        /// Get ex. [HEADER][CMD][ID][DATA_LEN][CS]
        /// Set ex. [HEADER][CMD][ID][DATA_LEN][DATA-1...DATA-N][CS]
        /// </summary>
        /// <param name="b">byte array</param>
        private void SendBytes(byte[] b)
        {
            // Command structure
            // [HEADER][CMD][ID][DATA_LEN][DATA-1]....[DATA-N][CHK_SUM]
            // PowerOn ex: 0xAA,0x11,0x01,0x01,0x01,0x01
            if (_lastCommandSentWasVolume) // If the last command sent was volume
            {
                if (b[1] != 0x12) // Check if this command is volume, and if not, delay this command
                {
                    CrestronEnvironment.Sleep(100);
                }
            }

            b[2] = Id;
            // append checksum by adding all bytes, except last which should be 00
            var checksum = 0;
            for (var i = 1; i < b.Length - 1; i++) // add 2nd through 2nd-to-last bytes
            {
                checksum += b[i];
            }
            checksum = checksum & 0x000000FF; // mask off MSBs
            b[b.Length - 1] = (byte)checksum;

            _lastCommandSentWasVolume = b[1] == 0x12;

            Communication.SendBytes(b);
        }

        /// <summary>
        /// Communication bytes received
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e">Event args</param>
        private void Communication_BytesReceived(object sender, GenericCommMethodReceiveBytesArgs e)
        {
            try
            {
                //Debug.Console(DebugLevelVerbose, this, "Received from e:{0}", ComTextHelper.GetEscapedText(e.Bytes));

                // Append the incoming bytes with whatever is in the buffer
                var newBytes = new byte[_incomingBuffer.Length + e.Bytes.Length];
                _incomingBuffer.CopyTo(newBytes, 0);
                e.Bytes.CopyTo(newBytes, _incomingBuffer.Length);

                // clear buffer
                //_incomingBuffer = _incomingBuffer.Skip(_incomingBuffer.Length).ToArray();

                if (Debug.Level == 2)
                {
                    // This check is here to prevent
                    // following string format from building unnecessarily on level 0 or 1
                    this.LogDebug("Received new bytes:{0}", ComTextHelper.GetEscapedText(newBytes));
                }

                // Get data length
                if (newBytes.Length >= 6)
                {
                    // check for header

                    // header + length + checksum
                    var dataLength = 5 + newBytes[3];
                    // Debug.Console(DebugLevelVerbose, this, "Got Data Length:{0} {1}", dataLength, newBytes[3]);
                    if (newBytes.Length >= dataLength)
                    {
                        var message = new byte[dataLength];
                        Array.Copy(newBytes, 0, message, 0, dataLength);
                        ParseMessage(message);
                        byte[] clear = { };
                        _incomingBuffer = clear;
                        return;
                    }
                }
                if (newBytes[0] == 0xAA)
                {
                    _incomingBuffer = newBytes;
                }
                else
                {
                    byte[] clear = { };
                    _incomingBuffer = clear;
                }
            }
            catch (Exception ex)
            {
                this.LogError("Exception parsing feedback: {message}", ex.Message);
                this.LogDebug(ex, "Stack trace: ");
            }
        }

        private void ParseMessage(byte[] message)
        {
            // input ack rx: {header}{command}{id}{dataLen}{ack/nak}{r-cmd}{val-1}{checksum}
            // input ack rx: { 0xAA }{ 0xFF  }{id}{ 0x03  }{'A'/'N'}{ 0x14}{input}{checksum}
            var command = message[5];

            switch (command)
            {
                // General status
                case SamsungMdcCommands.StatusControl:
                {
                    //UpdatePowerFB(message[2], message[5]); // "power" can be misrepresented when the display sleeps
                    // Handle the first power on fb when waiting for it.
                    if (_isPoweringOnIgnorePowerFb && message[6] == SamsungMdcCommands.PowerOn)
                    {
                        _isPoweringOnIgnorePowerFb = false;
                    }
                    // Ignore general-status power off messages when powering up
                    // if (!(_isPoweringOnIgnorePowerFb && message[6] == PowerOff))
                    UpdatePowerFb(message[6]);
                    UpdateVolumeFb(message[7]);
                    UpdateMuteFb(message[8]);
                    UpdateInputFb(message[9]);
                    if (Debug.Level == 2)
                    {
                        // This check is here to prevent following string format from building unnecessarily on level 0 or 1
                        this.LogVerbose(
                            "StatusControl Power-'{0}d', Input-'{1}d',  Volume-'{2}d', Mute-'{3}d'",
                            message[6],
                            message[9],
                            message[7],
                            message[8]
                        );
                    }
                    break;
                }
                // Power status
                case SamsungMdcCommands.PowerControl:
                {
                    this.LogVerbose("PowerControl-'{0}d'", message[6]);
                    UpdatePowerFb(message[6]);
                    break;
                }
                // Volume level
                case SamsungMdcCommands.VolumeControl:
                {
                    if (message[6] != _lastVolumeFeedbackReceived)
                    {
                        this.LogVerbose("VolumeControl-'{0}d'", message[6]);
                        _lastVolumeFeedbackReceived = message[6];
                    }
                    UpdateVolumeFb(message[6]);
                    break;
                }
                // Volume mute status
                case SamsungMdcCommands.MuteControl:
                {
                    if (message[6] != _lastMuteFeedbackReceived)
                    {
                        this.LogVerbose("MuteControl-'{0}d'", message[6]);
                        _lastMuteFeedbackReceived = message[6];
                    }
                    UpdateMuteFb(message[6]);
                    break;
                }
                // Input status
                case SamsungMdcCommands.InputSourceControl:
                {
                    if (message[6] != _lastInputFeedbackReceived)
                    {
                        this.LogVerbose("InputSourceControl-'{0}d'", message[6]);
                        _lastInputFeedbackReceived = message[6];
                    }
                    UpdateInputFb(message[6]);
                    break;
                }
                // Monitoring (Sub CMD 0x84, 2.1.D0.84 Monitoring, pdf page 228 or 244)
                // msg[0] = Header, 0xAA
                // msg[1] = Command, 0xFF
                // msg[2] = ID
                // msg[3] = Data Length
                // msg[4] = Ack/Nack
                // msg[5] = r-CMD, 0xD0
                // msg[6] = Sub CMD, 0x84
                // msg[7] = va1, power&IC
                // msg[8] = val2, HDBT status
                // msg[9] = val3, Temperature
                // msg[10] = val4, illuminance
                // msg[11] = val5, module1
                // msg[12] = val6, module1 LED error data
                // msg[13] = val7, module2
                // msg[14] = val8, module2 LED error data
                // msg[n] = val-n, moduleN
                // msg[n+1] = val-n+1, moduleN LED error data
                case SamsungMdcCommands.LedProductFeature:
                {
                    this.LogVerbose(
                        "LedProductFeature SubCmd{0} DataLen{1}",
                        message[6],
                        message[3]
                    );

                    if (message[6] == SamsungMdcCommands.LedSubcmdMonitoring)
                    {
                        this.LogVerbose(
                            "LedProductFeature SubCmd{0} Temperature{1}",
                            message[6],
                            message[9]
                        );
                        UpdateLedTemperatureFb(message[9]);
                    }
                    break;
                }
                // Serial number control
                case SamsungMdcCommands.SerialNumberControl:
                {
                    var serialNumber = new byte[18];
                    Array.Copy(message, 6, serialNumber, 0, 18);

                    UpdateSerialNumber(serialNumber);
                    break;
                }
                // Software version control
                case SamsungMdcCommands.SwVersionControl:
                {
                    var length = message[3];

                    var firmware = new byte[length];

                    Array.Copy(message, 6, firmware, 0, length);

                    UpdateFirmwareVersion(firmware);
                    break;
                }
                // Network info
                case SamsungMdcCommands.SystemConfiguration:
                {
                    var length = message[3];
                    if (message[4] == SamsungMdcCommands.NetworkConfiguration)
                    {
                        var ipAddressInfo = new byte[length - 1];

                        Array.Copy(message, 7, ipAddressInfo, 0, length - 1);

                        UpdateNetworkInfo(ipAddressInfo);
                        break;
                    }

                    if (message[4] == SamsungMdcCommands.SystemConfigurationMacAddress)
                    {
                        var macInfo = new byte[length - 1];

                        Array.Copy(message, 6, macInfo, 0, length - 1);

                        UpdateMacAddress(macInfo);
                    }

                    break;
                }
                default:
                {
                    this.LogVerbose("Unknown message: {0}", ComTextHelper.GetEscapedText(message));
                    break;
                }
            }
        }

        #region Inits

        /// <summary>
        /// Initialize
        /// </summary>
        private void Init()
        {
            WarmupTime = _warmingTimeMs >= 0 ? _warmingTimeMs : 15000;
            CooldownTime = _coolingTimeMs >= 0 ? _coolingTimeMs : 15000;

            InitCommMonitor();

            InitVolumeControls();

            InitInputPortsAndFeedbacks();

            InitTemperatureFeedback();

            StatusGet();

            SetupInputs();
        }

        private void InitCommMonitor()
        {
            var pollInterval = _pollIntervalMs > 0 ? _pollIntervalMs : 30000;

            CommunicationMonitor = new GenericCommunicationMonitor(
                this,
                Communication,
                pollInterval,
                180000,
                300000,
                StatusGet,
                true
            );

            DeviceManager.AddDevice(CommunicationMonitor);

            StatusFeedback = new IntFeedback(() => (int)CommunicationMonitor.Status);

            CommunicationMonitor.StatusChange += (sender, args) =>
            {
                this.LogVerbose("Device status: {0}", CommunicationMonitor.Status);
                StatusFeedback.FireUpdate();
            };
        }

        private void InitVolumeControls()
        {
            if (_upperLimit != _lowerLimit && _upperLimit > _lowerLimit)
            {
                ScaleVolume = true;
            }

            if (!ScaleVolume)
            {
                _volumeIncrementer = new ActionIncrementer(
                    655,
                    0,
                    65535,
                    800,
                    80,
                    v => SetVolumeInternal((ushort)v, skipDebounce: true),
                    () => _lastVolumeSent
                );
            }
            else
            {
                var scaleUpper = NumericalHelpers.Scale(_upperLimit, 0, 100, 0, 65535);
                var scaleLower = NumericalHelpers.Scale(_lowerLimit, 0, 100, 0, 65535);

                _volumeIncrementer = new ActionIncrementer(
                    655,
                    (int)scaleLower,
                    (int)scaleUpper,
                    800,
                    80,
                    v => SetVolumeInternal((ushort)v, skipDebounce: true),
                    () => _lastVolumeSent
                );
            }

            VolumeLevelFeedback = new IntFeedback(() => _volumeLevelForSig);
            MuteFeedback = new BoolFeedback(() => _isMuted);
        }

        private void InitInputPortsAndFeedbacks()
        {
            //_InputFeedback = new List<bool>();
            InputFeedback = new List<BoolFeedback>();
            if (_customInputs.Count == 0)
            {
                AddRoutingInputPort(
                    new RoutingInputPort(
                        RoutingPortNames.HdmiIn1,
                        eRoutingSignalType.Audio | eRoutingSignalType.Video,
                        eRoutingPortConnectionType.Hdmi,
                        new Action(InputHdmi1),
                        this
                    ),
                    SamsungMdcCommands.InputHdmi1
                );

                AddRoutingInputPort(
                    new RoutingInputPort(
                        RoutingPortNames.HdmiIn2,
                        eRoutingSignalType.Audio | eRoutingSignalType.Video,
                        eRoutingPortConnectionType.Hdmi,
                        new Action(InputHdmi2),
                        this
                    ),
                    SamsungMdcCommands.InputHdmi2
                );

                AddRoutingInputPort(
                    new RoutingInputPort(
                        RoutingPortNames.HdmiIn3,
                        eRoutingSignalType.Audio | eRoutingSignalType.Video,
                        eRoutingPortConnectionType.Hdmi,
                        new Action(InputHdmi3),
                        this
                    ),
                    SamsungMdcCommands.InputHdmi3
                );

                AddRoutingInputPort(
                    new RoutingInputPort(
                        RoutingPortNames.HdmiIn4,
                        eRoutingSignalType.Audio | eRoutingSignalType.Video,
                        eRoutingPortConnectionType.Hdmi,
                        new Action(InputHdmi4),
                        this
                    ),
                    SamsungMdcCommands.InputHdmi4
                );

                AddRoutingInputPort(
                    new RoutingInputPort(
                        RoutingPortNames.DisplayPortIn1,
                        eRoutingSignalType.Audio | eRoutingSignalType.Video,
                        eRoutingPortConnectionType.DisplayPort,
                        new Action(InputDisplayPort1),
                        this
                    ),
                    SamsungMdcCommands.InputDisplayPort1
                );

                AddRoutingInputPort(
                    new RoutingInputPort(
                        RoutingPortNames.DisplayPortIn2,
                        eRoutingSignalType.Audio | eRoutingSignalType.Video,
                        eRoutingPortConnectionType.DisplayPort,
                        new Action(InputDisplayPort2),
                        this
                    ),
                    SamsungMdcCommands.InputDisplayPort2
                );

                AddRoutingInputPort(
                    new RoutingInputPort(
                        RoutingPortNames.DviIn,
                        eRoutingSignalType.Audio | eRoutingSignalType.Video,
                        eRoutingPortConnectionType.Dvi,
                        new Action(InputDvi1),
                        this
                    ),
                    SamsungMdcCommands.InputDvi1
                );

                AddRoutingInputPort(
                    new RoutingInputPort(
                        RoutingPortNames.MediaPlayer,
                        eRoutingSignalType.Audio | eRoutingSignalType.Video,
                        eRoutingPortConnectionType.Streaming,
                        new Action(InputMagicInfo),
                        this
                    ),
                    SamsungMdcCommands.InputMagicInfo
                );

                AddRoutingInputPort(
                    new RoutingInputPort(
                        "usb",
                        eRoutingSignalType.UsbInput | eRoutingSignalType.UsbOutput,
                        eRoutingPortConnectionType.Streaming,
                        null,
                        this
                    ),
                    0x99
                );
            }
            else
            {
                foreach (var customInput in _customInputs)
                {
                    var input = customInput;
                    var commandHex = ConvertStringToHex(input.InputCommand);

                    AddRoutingInputPort(
                        new RoutingInputPort(
                            input.InputIdentifier,
                            eRoutingSignalType.Audio | eRoutingSignalType.Video,
                            (eRoutingPortConnectionType)
                                Enum.Parse(
                                    typeof(eRoutingPortConnectionType),
                                    input.InputConnector,
                                    true
                                ),
                            new Action(() => InputGeneric(commandHex)),
                            this
                        ),
                        commandHex
                    );
                }
            }

            for (var i = 0; i < InputPorts.Count; i++)
            {
                var j = i + 1;
                InputFeedback.Add(new BoolFeedback(() => CurrentInputNumber == j));
            }

            InputNumberFeedback = new IntFeedback(() =>
            {
                this.LogVerbose(
                    "InputNumberFeedback: CurrentInputNumber-'{0}'",
                    CurrentInputNumber
                );
                return CurrentInputNumber;
            });
        }

        private void InitTemperatureFeedback()
        {
            CurrentLedTemperatureCelsiusFeedback = new IntFeedback(() =>
            {
                this.LogVerbose("Current Temperature Celsius {0}", _currentLedTemperatureCelsius);
                return _currentLedTemperatureCelsius;
            });
            CurrentLedTemperatureFahrenheitFeedback = new IntFeedback(() =>
            {
                this.LogVerbose(
                    "Current Temperature Fahrenheit {0}",
                    _currentLedTemperatureFahrenheit
                );
                return _currentLedTemperatureFahrenheit;
            });
        }

        #endregion


        /// <summary>
        /// Status control (Cmd: 0x00) pdf page 26
        /// Get: [HEADER=0xAA][Cmd=0x00][ID][DATA_LEN=0x00][CS=0x00]
        /// </summary>
        public void StatusGet()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.StatusControl,
                    0x00,
                    0x00,
                    0x00,
                }
            );

            //_pollRing = null;
            //PollRing = new CTimer(o => VolumeGet(), null, 1000);
            //_pollRing = new CTimer(o => LedProductMonitorGet(), null, 10000);

            if (_pollLedTemps)
                SendBytes(
                    new byte[]
                    {
                        SamsungMdcCommands.Header,
                        SamsungMdcCommands.LedProductFeature,
                        0x00,
                        0x01,
                        SamsungMdcCommands.LedSubcmdMonitoring,
                        0x00,
                    }
                );
        }

        #region Power

        /// <summary>
        /// Power on (Cmd: 0x11) pdf page 42
        /// Set: [HEADER=0xAA][Cmd=0x11][ID][DATA_LEN=0x01][DATA-1=0x01][CS=0x00]
        /// </summary>
        public override void PowerOn()
        {
            _isPoweringOnIgnorePowerFb = true;

            if (_powerIsOn || _isWarmingUp || _isCoolingDown)
            {
                return;
            }

            _powerIsOn = true;

            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.PowerControl,
                    0x00,
                    0x01,
                    SamsungMdcCommands.PowerOn,
                    0x00,
                }
            );

            if (WarmupTimer != null || WarmupTime == 0)
            {
                PowerIsOnFeedback.FireUpdate();
                return;
            }

            _isWarmingUp = true;
            IsWarmingUpFeedback.FireUpdate();

            // Fake power-up cycle
            WarmupTimer = new CTimer(
                o =>
                {
                    WarmupTimer = null;
                    _isWarmingUp = false;

                    IsWarmingUpFeedback.FireUpdate();
                    PowerIsOnFeedback.FireUpdate();
                },
                WarmupTime
            );
        }

        /// <summary>
        /// Power off (Cmd: 0x11) pdf page 42
        /// Set: [HEADER=0xAA][Cmd=0x11][ID][DATA_LEN=0x01][DATA-1=0x00][CS=0x00]
        /// </summary>
        public override void PowerOff()
        {
            _isPoweringOnIgnorePowerFb = false;
            // If a display has unreliable-power off feedback, just override this and
            // remove this check.
            if (!_powerIsOn || _isWarmingUp || _isCoolingDown)
            {
                return;
            }

            _powerIsOn = false;

            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.PowerControl,
                    0x00,
                    0x01,
                    SamsungMdcCommands.PowerOff,
                    0x00,
                }
            );

            CurrentInputNumber = 0;

            InputNumberFeedback.FireUpdate();

            if (CooldownTimer != null || CooldownTime == 0)
            {
                PowerIsOnFeedback.FireUpdate();
                return;
            }

            _isCoolingDown = true;

            IsCoolingDownFeedback.FireUpdate();

            // Fake cool-down cycle
            CooldownTimer = new CTimer(
                o =>
                {
                    CooldownTimer = null;
                    _isCoolingDown = false;
                    PowerIsOnFeedback.FireUpdate();
                    IsCoolingDownFeedback.FireUpdate();
                },
                CooldownTime
            );
        }

        /// <summary>
        /// Power toggle (Cmd: 0x11) pdf page 42
        /// Set: [HEADER=0xAA][Cmd=0x11][ID][DATA_LEN=0x01][DATA-1=0x01||0x00][CS=0x00]
        /// </summary>
        public override void PowerToggle()
        {
            if (PowerIsOnFeedback.BoolValue && !IsWarmingUpFeedback.BoolValue)
            {
                PowerOff();
            }
            else if (!PowerIsOnFeedback.BoolValue && !IsCoolingDownFeedback.BoolValue)
            {
                PowerOn();
            }
        }

        /// <summary>
        /// Power on (Cmd: 0x11) pdf page 42
        /// Get: [HEADER=0xAA][Cmd=0x11][ID][DATA_LEN=0x00][CS=0x00]
        /// </summary>
        public void PowerGet()
        {
            //SendBytes(PowerGetCmd);
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.PowerControl,
                    0x00,
                    0x00,
                    0x00,
                }
            );
        }

        /// <summary>
        /// Power feedback
        /// </summary>
        private void UpdatePowerFb(byte powerByte)
        {
            var newVal = powerByte == 1;
            if (!newVal)
            {
                CurrentInputNumber = 0;
            }
            if (newVal == _powerIsOn)
            {
                return;
            }
            _powerIsOn = newVal;
            PowerIsOnFeedback.FireUpdate();
        }

        #endregion


        #region Inputs, InputPorts and ExecuteSwitch

        /// <summary>
        /// Add routing input port
        /// </summary>
        /// <param name="port"></param>
        /// <param name="fbMatch"></param>
        private void AddRoutingInputPort(RoutingInputPort port, byte fbMatch)
        {
            port.FeedbackMatchObject = fbMatch;
            InputPorts.Add(port);
        }

        /// <summary>
        /// Executes a switch, turning on display if necessary.
        /// </summary>
        /// <param name="selector"></param>
        public override void ExecuteSwitch(object selector)
        {
            if (selector is null)
            {
                return;
            }

            if (_powerIsOn)
            {
                if (selector is Action actionToExecute)
                {
                    actionToExecute();
                }
            }
            else // if power is off, wait until we get on FB to send it.
            {
                // One-time event handler to wait for power on before executing switch
                EventHandler<FeedbackEventArgs> handler = null; // necessary to allow reference inside lambda to handler
                handler = (o, a) =>
                {
                    if (_isWarmingUp)
                    {
                        return;
                    }

                    IsWarmingUpFeedback.OutputChange -= handler;

                    if (selector is Action actionToExecute)
                    {
                        actionToExecute();
                    }
                };
                IsWarmingUpFeedback.OutputChange += handler; // attach and wait for on FB
                PowerOn();
            }
        }

        private void SetupInputs()
        {
            if (_config.ActiveInputs != null && _config.ActiveInputs.Count > 0)
            {
                Inputs = new SamsungInputs { Items = new Dictionary<byte, ISelectableItem>() };

                var activeInputsMap = _config.ActiveInputs.ToDictionary(
                    ai => ai.Key,
                    ai => ai.Name
                );

                var allInputs = new Dictionary<string, KeyValuePair<byte, SamsungInput>>
                {
                    {
                        "hdmi1",
                        new KeyValuePair<byte, SamsungInput>(
                            SamsungMdcCommands.InputHdmi1,
                            new SamsungInput("hdmi1", "HDMI 1", this, InputHdmi1)
                        )
                    },
                    {
                        "hdmi2",
                        new KeyValuePair<byte, SamsungInput>(
                            SamsungMdcCommands.InputHdmi2,
                            new SamsungInput("hdmi2", "HDMI 2", this, InputHdmi2)
                        )
                    },
                    {
                        "hdmi3",
                        new KeyValuePair<byte, SamsungInput>(
                            SamsungMdcCommands.InputHdmi3,
                            new SamsungInput("hdmi3", "HDMI 3", this, InputHdmi3)
                        )
                    },
                    {
                        "hdmi4",
                        new KeyValuePair<byte, SamsungInput>(
                            SamsungMdcCommands.InputHdmi4,
                            new SamsungInput("hdmi4", "HDMI 4", this, InputHdmi4)
                        )
                    },
                    {
                        "displayPort1",
                        new KeyValuePair<byte, SamsungInput>(
                            SamsungMdcCommands.InputDisplayPort1,
                            new SamsungInput(
                                "displayPort1",
                                "Display Port 1",
                                this,
                                InputDisplayPort1
                            )
                        )
                    },
                    {
                        "displayPort2",
                        new KeyValuePair<byte, SamsungInput>(
                            SamsungMdcCommands.InputDisplayPort2,
                            new SamsungInput(
                                "displayPort2",
                                "Display Port 2",
                                this,
                                InputDisplayPort2
                            )
                        )
                    },
                    {
                        "dvi1",
                        new KeyValuePair<byte, SamsungInput>(
                            SamsungMdcCommands.InputDvi1,
                            new SamsungInput("dvi1", "DVI 1", this, InputDvi1)
                        )
                    },
                    {
                        "magicInfo",
                        new KeyValuePair<byte, SamsungInput>(
                            SamsungMdcCommands.InputMagicInfo,
                            new SamsungInput("magicInfo", "Magic Info", this, InputMagicInfo)
                        )
                    },
                };

                foreach (var activeInput in activeInputsMap)
                {
                    if (allInputs.TryGetValue(activeInput.Key, out var input))
                    {
                        Inputs.Items.Add(
                            input.Key,
                            new SamsungInput(
                                input.Value.Key,
                                activeInput.Value,
                                this,
                                input.Value.Select
                            )
                        );
                    }
                }
            }
            else
            {
                Inputs = new SamsungInputs
                {
                    Items = new Dictionary<byte, ISelectableItem>
                    {
                        {
                            SamsungMdcCommands.InputHdmi1,
                            new SamsungInput("hdmi1", "HDMI 1", this, InputHdmi1)
                        },
                        {
                            SamsungMdcCommands.InputHdmi2,
                            new SamsungInput("hdmi2", "HDMI 2", this, InputHdmi2)
                        },
                        {
                            SamsungMdcCommands.InputHdmi3,
                            new SamsungInput("hdmi3", "HDMI 3", this, InputHdmi3)
                        },
                        {
                            SamsungMdcCommands.InputHdmi4,
                            new SamsungInput("hdmi4", "HDMI 4", this, InputHdmi4)
                        },
                        {
                            SamsungMdcCommands.InputDisplayPort1,
                            new SamsungInput(
                                "displayPort1",
                                "Display Port 1",
                                this,
                                InputDisplayPort1
                            )
                        },
                        {
                            SamsungMdcCommands.InputDisplayPort2,
                            new SamsungInput(
                                "displayPort2",
                                "Display Port 2",
                                this,
                                InputDisplayPort2
                            )
                        },
                        {
                            SamsungMdcCommands.InputDvi1,
                            new SamsungInput("dvi1", "DVI 1", this, InputDvi1)
                        },
                        {
                            SamsungMdcCommands.InputMagicInfo,
                            new SamsungInput("magicInfo", "Magic Info", this, InputMagicInfo)
                        },
                    },
                };
            }
        }

        /// <summary>
        /// Input HDMI 1 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x21][CS=0x00]
        /// </summary>
        public void InputHdmi1()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.InputSourceControl,
                    0x00,
                    0x01,
                    SamsungMdcCommands.InputHdmi1,
                    0x00,
                }
            );
        }

        /// <summary>
        /// Input HDMI 2 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x23][CS=0x00]
        /// </summary>
        public void InputHdmi2()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.InputSourceControl,
                    0x00,
                    0x01,
                    SamsungMdcCommands.InputHdmi2,
                    0x00,
                }
            );
        }

        /// <summary>
        /// Input HDMI 3 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x31][CS=0x00]
        /// </summary>
        public void InputHdmi3()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.InputSourceControl,
                    0x00,
                    0x01,
                    SamsungMdcCommands.InputHdmi3,
                    0x00,
                }
            );
        }

        /// <summary>
        /// Input HDMI 4 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x33][CS=0x00]
        /// </summary>
        public void InputHdmi4()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.InputSourceControl,
                    0x00,
                    0x01,
                    SamsungMdcCommands.InputHdmi4,
                    0x00,
                }
            );
        }

        /// <summary>
        /// Input DisplayPort 1 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x25][CS=0x00]
        /// </summary>
        public void InputDisplayPort1()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.InputSourceControl,
                    0x00,
                    0x01,
                    SamsungMdcCommands.InputDisplayPort1,
                    0x00,
                }
            );
        }

        /// <summary>
        /// Input DisplayPort 2 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x26][CS=0x00]
        /// </summary>
        public void InputDisplayPort2()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.InputSourceControl,
                    0x00,
                    0x01,
                    SamsungMdcCommands.InputDisplayPort2,
                    0x00,
                }
            );
        }

        /// <summary>
        /// Input DVI 1 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x18][CS=0x00]
        /// </summary>
        public void InputDvi1()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.InputSourceControl,
                    0x00,
                    0x01,
                    SamsungMdcCommands.InputDvi1,
                    0x00,
                }
            );
        }

        /// <summary>
        /// Input DVI 1 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x20][CS=0x00]
        /// </summary>
        public void InputMagicInfo()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.InputSourceControl,
                    0x00,
                    0x01,
                    SamsungMdcCommands.InputMagicInfo,
                    0x00,
                }
            );
        }

        /// <summary>
        /// Input HDMI 1 (Cmd: 0x14) pdf page 426
        /// Get: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x00][CS=0x00]
        /// </summary>
        public void InputGet()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.InputSourceControl,
                    0x00,
                    0x00,
                    0x00,
                }
            );
        }

        public void InputGeneric(byte data)
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.InputSourceControl,
                    0x00,
                    0x01,
                    data,
                    0x00,
                }
            );
        }

        public static byte ConvertStringToHex(string src)
        {
            return (byte)int.Parse(src.Substring(0, 2), NumberStyles.HexNumber);
        }

        /// <summary>
        /// Input feedback
        /// </summary>
        private void UpdateInputFb(byte b)
        {
            var newInput = InputPorts.FirstOrDefault(i => i.FeedbackMatchObject.Equals(b));

            if (newInput == null)
                return;

            int inputIndex;

            try
            {
                inputIndex = InputPorts.FindIndex(a => a.FeedbackMatchObject.Equals(b)) + 1;
            }
            catch (Exception e)
            {
                this.LogError(
                    "Unable to match feedback {input} with input. Exception : {message}",
                    b,
                    e.Message
                );
                this.LogDebug(e, "Stack trace: ");
                return;
            }

            CurrentInputPort = newInput;

            CurrentInputNumber = inputIndex;

            if (Inputs.Items.ContainsKey(b))
            {
                foreach (var item in Inputs.Items)
                {
                    item.Value.IsSelected = item.Key.Equals(b);
                }
            }

            Inputs.CurrentItem = b;
        }

        private void UpdateBooleanFeedback()
        {
            try
            {
                foreach (var item in InputFeedback)
                {
                    item.FireUpdate();
                }
            }
            catch (Exception e)
            {
                this.LogError("Exception updating boolean feedbacks: {message}", e.Message);
                this.LogDebug(e, "Stack trace: ");
            }
        }

        #endregion


        #region IBasicVolumeWithFeedback Members

        /// <summary>
        /// Volume level feedback property
        /// </summary>
        public IntFeedback VolumeLevelFeedback { get; private set; }

        /// <summary>
        /// volume mte feedback property
        /// </summary>
        public BoolFeedback MuteFeedback { get; private set; }

        /// <summary>
        /// Scales the level to the range of the display and sends the command
        /// Volume level control (Cmd: 0x12) pdf page 44
        /// Level range 0d - 100d (0x00 - 0x64)
        /// Set: [HEADER=0xAA][Cmd=0x12][ID][DATA_LEN=0x01][DATA-1=(Scaled)][CS=0x00]
        /// </summary>
        /// <param name="level"></param>
        public void SetVolume(ushort level)
        {
            // Fader movements use debounce (default)
            SetVolumeInternal(level, skipDebounce: false);
        }

        /// <summary>
        /// Internal method for volume setting with optional debounce bypass
        /// </summary>
        private void SetVolumeInternal(ushort level, bool skipDebounce = false)
        {
            if (skipDebounce)
            {
                // Send immediately for button presses
                SendVolumeCommand(level);
            }
            else
            {
                // Debounce for fader movements
                _queuedVolumeLevel = level;
                
                // Restart the debounce timer - waits 500ms after last change before sending
                if (_volumeDebounceTimer != null)
                {
                    _volumeDebounceTimer.Stop();
                    _volumeDebounceTimer.Dispose();
                }

                _volumeDebounceTimer = new CTimer(
                    obj =>
                    {
                        // Send the volume command after 500ms of no changes
                        SendVolumeCommand(_queuedVolumeLevel);
                        // Clear the timer after it fires so feedback can be fired on device response
                        _volumeDebounceTimer = null;
                    },
                    null,
                    500  // 500ms debounce
                );
            }
        }

        /// <summary>
        /// Internal method to send volume command to device
        /// </summary>
        private void SendVolumeCommand(ushort level)
        {
            int scaled;
            _lastVolumeSent = level;
            if (!ScaleVolume)
            {
                scaled = (int)NumericalHelpers.Scale(level, 0, 65535, 0, 100);
            }
            else
            {
                scaled = (int)NumericalHelpers.Scale(level, 0, 65535, _lowerLimit, _upperLimit);
            }
            
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.VolumeControl,
                    0x00,
                    0x01,
                    Convert.ToByte(scaled),
                    0x00,
                }
            );
            
            if (_isMuted)
            {
                MuteOff();
            }
        }

        /// <summary>
        /// Volume down (decrement)
        /// </summary>
        /// <param name="pressRelease"></param>
        public void VolumeDown(bool pressRelease)
        {
            if (pressRelease)
            {
                if (_isMuted)
                {
                    MuteOff();
                }
                _volumeIncrementer.StartDown();
                _volumeIsRamping = true;
            }
            else
            {
                _volumeIsRamping = false;
                _volumeIncrementer.Stop();
            }
        }

        /// <summary>
        /// Volume up (increment)
        /// </summary>
        /// <param name="pressRelease"></param>
        public void VolumeUp(bool pressRelease)
        {
            if (pressRelease)
            {
                if (_isMuted)
                {
                    MuteOff();
                }
                _volumeIncrementer.StartUp();
                _volumeIsRamping = true;
            }
            else
            {
                _volumeIsRamping = false;
                _volumeIncrementer.Stop();
            }
        }

        /// <summary>
        /// Volume level control (Cmd: 0x12) pdf page 44
        /// Level range 0d - 100d (0x00 - 0x64)
        /// Get: [HEADER=0xAA][Cmd=0x12][ID][DATA_LEN=0x00][CS=0x00]
        /// </summary>
        public void VolumeGet()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.VolumeControl,
                    0x00,
                    0x00,
                    0x00,
                }
            );
            if (_pollTimer != null)
            {
                _pollTimer.Reset(1000);
                return;
            }
            _pollTimer = new CTimer(o => MuteGet(), null, 1000);
        }

        // <summary>
        // Volume feedback
        // </summary>
        private void UpdateVolumeFb(byte b)
        {
            ushort newVol;
            if (!ScaleVolume)
            {
                newVol = (ushort)NumericalHelpers.Scale(b, 0, 100, 0, 65535);
            }
            else
            {
                newVol = (ushort)NumericalHelpers.Scale(b, _lowerLimit, _upperLimit, 0, 65535);
            }
            if (!_volumeIsRamping)
            {
                _lastVolumeSent = newVol;
            }

            // Always update the internal state
            _volumeLevelForSig = newVol;
            
            // Only fire feedback if debounce timer is not active (i.e., not awaiting user to finish adjusting fader)
            // This prevents echo loops while still updating UI on device changes
            if (_volumeDebounceTimer == null)
            {
                VolumeLevelFeedback.FireUpdate();
            }
        }

        /// <summary>
        /// Mute off (Cmd: 0x13) pdf page 45
        /// Set: [Header=0xAA][Cmd=0x13][ID][DATA_LEN=0x01][DATA-1=0x00][CS=0x00]
        /// </summary>
        public void MuteOff()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.MuteControl,
                    0x00,
                    0x01,
                    SamsungMdcCommands.MuteOff,
                    0x00,
                }
            );
        }

        /// <summary>
        /// Mute on (Cmd: 0x13) pdf page 45
        /// Set: [Header=0xAA][Cmd=0x13][ID][DATA_LEN=0x01][DATA-1=0x01][CS=0x00]
        /// </summary>
        public void MuteOn()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.MuteControl,
                    0x00,
                    0x01,
                    SamsungMdcCommands.MuteOn,
                    0x00,
                }
            );
        }

        /// <summary>
        /// Mute toggle
        /// </summary>
        public void MuteToggle()
        {
            if (_isMuted)
            {
                MuteOff();
            }
            else
            {
                MuteOn();
            }
        }

        /// <summary>
        /// Mute get (Cmd: 0x13) pdf page 45
        /// Get: [Header=0xAA][Cmd=0x13][ID][DATA_LEN=0x00][CS=0x00]
        /// </summary>
        public void MuteGet()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.MuteControl,
                    0x00,
                    0x00,
                    0x00,
                }
            );
        }

        /// <summary>
        /// Mute feedback
        /// </summary>
        private void UpdateMuteFb(byte b)
        {
            _isMuted = b == 1;

            MuteFeedback.FireUpdate();
        }

        #endregion


        #region System & Network Information

        private void UpdateMacAddress(byte[] macInfo)
        {
            DeviceInfo.MacAddress = string.Format(
                "{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                macInfo[0],
                macInfo[1],
                macInfo[2],
                macInfo[3],
                macInfo[4],
                macInfo[5]
            );

            OnDeviceInfoChange();
        }

        private void UpdateNetworkInfo(byte[] ipAddressInfo)
        {
            var ipAddress = new byte[4];

            Array.Copy(ipAddressInfo, 0, ipAddress, 0, 4);

            DeviceInfo.IpAddress = string.Format(
                "{0}.{1}.{2}.{3}",
                ipAddress[0],
                ipAddress[1],
                ipAddress[2],
                ipAddress[3]
            );

            OnDeviceInfoChange();
        }

        private void UpdateFirmwareVersion(byte[] firmware)
        {
            var version = Encoding.UTF8.GetString(firmware, 0, 12);

            DeviceInfo.FirmwareVersion = version;

            OnDeviceInfoChange();
        }

        private void UpdateSerialNumber(byte[] serialNumber)
        {
            var serialNumberString = Encoding.UTF8.GetString(serialNumber, 0, serialNumber.Length);

            DeviceInfo.SerialNumber = serialNumberString;

            OnDeviceInfoChange();
        }

        private void OnDeviceInfoChange()
        {
            var handler = DeviceInfoChanged;

            if (handler == null)
            {
                return;
            }

            handler(this, new DeviceInfoEventArgs { DeviceInfo = DeviceInfo });
        }

        #endregion


        #region Led Information & Monitoring

        /// <summary>
        /// Temeprature Control (Cmd: 0x85) pdf page 142
        /// Get: [HEADER=0xAA][Cmd=0x85][ID][DATA_LEN=0x00][CS=0x00]
        /// </summary>
        public void TemperatureMaxGet()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.TemperatureControl,
                    0x00,
                    0x00,
                    0x00,
                }
            );
        }

        /// <summary>
        /// LED Product (Cmd: 0xD0) pdf page 221
        /// LED Product temperature (subcmd: 0x84) pdf page 228
        /// Get: [HHEADER=0xAA][Cmd=0xD0][ID][DATA_LEN=0x01][SUBCMD=0x84][CS=0x00]
        /// </summary>
        public void LedProductMonitorGet()
        {
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.LedProductFeature,
                    0x00,
                    0x01,
                    SamsungMdcCommands.LedSubcmdMonitoring,
                    0x00,
                }
            );
        }

        /// <summary>
        /// Current LED Product Monitor Temperature feedback
        /// </summary>
        private void UpdateLedTemperatureFb(byte b)
        {
            // Temperature: 0-254 (Celsius)
            int temp = Convert.ToInt16(b);

            // scaler if needed
            //int tempScaled = (int)NumericalHelpers.Scale(temp, 0, 65535, 0, 254);

            CurrentLedTemperatureCelsius = temp;
            CurrentLedTemperatureFahrenheit = (int)ConvertCelsiusToFahrenheit(temp);
        }

        private double ConvertCelsiusToFahrenheit(double c)
        {
            var f = (c * 9 / 5) + 32;

            this.LogVerbose("ConvertCelsiusToFahrenheit: Fahrenheit = {0}, Celsius = {1}", f, c);

            return f;
        }

        #endregion


        #region Implementation of IDeviceInfoProvider

        //needs testing
        public void UpdateDeviceInfo()
        {
            /*This is an example structure used in other Get Commands
            SendBytes(new byte[] { SamsungMdcCommands.Header, SamsungMdcCommands.PowerControl, 0x00, 0x00, 0x00 });
            */

            if (DeviceInfo == null)
            {
                DeviceInfo = new DeviceInfo();
            }

            //get serial number (0x0B)
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.SerialNumberControl,
                    0x00,
                    0x00,
                    0x00,
                }
            );
            //get firmware version (0x0E)
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.SwVersionControl,
                    0x00,
                    0x00,
                    0x00,
                }
            );
            //get IP Info (0x1B, 0x82)
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.NetworkConfiguration,
                    0x00,
                    0x00,
                    0x00,
                }
            );
            //get MAC address (0x1B, 0x81)
            SendBytes(
                new byte[]
                {
                    SamsungMdcCommands.Header,
                    SamsungMdcCommands.SystemConfigurationMacAddress,
                    0x00,
                    0x00,
                    0x00,
                }
            );
        }

        public DeviceInfo DeviceInfo { get; private set; }
        public event DeviceInfoChangeHandler DeviceInfoChanged;

        #endregion
    }
}
