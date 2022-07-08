﻿// For Basic SIMPL# Classes
// For Basic SIMPL#Pro classes

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro.DeviceSupport;
using Newtonsoft.Json;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.DeviceInfo;
using PepperDash.Essentials.Core.Routing;
using Feedback = PepperDash.Essentials.Core.Feedback;

namespace PepperDash.Plugin.Display.SamsungMdc
{
    public class SamsungMdcDisplayController : TwoWayDisplayBase, IBasicVolumeWithFeedback, ICommunicationMonitor,
        IBridgeAdvanced, IDeviceInfoProvider
    {
        public const int InputPowerOn = 101;
        public const int InputPowerOff = 102;        
        public static List<string> InputKeys = new List<string>();
        private readonly SamsungMDCDisplayPropertiesConfig _config;
        private readonly uint _coolingTimeMs;

        private readonly int _lowerLimit;
        private readonly long _pollIntervalMs;
        private readonly int _upperLimit;
        private readonly uint _warmingTimeMs;
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

        private byte[] _incomingBuffer = {};

        public List<BoolFeedback> InputFeedback;
        private RoutingInputPort _currentInputPort;
        
        public IntFeedback InputNumberFeedback;
        private int _currentInputNumber;
		public int CurrentInputNumber
		{
			get
			{
				return _currentInputNumber;
			}
			private set
			{
				_currentInputNumber = value;
				InputNumberFeedback.FireUpdate();
				UpdateBooleanFeedback();
			}
		}
        public int SetInput
        {
            get
            {
                Debug.Console(DebugLevelDebug, this, "SetInput: CurrentInputNumber-'{0}'", CurrentInputNumber);
                return CurrentInputNumber;
            }
            set
            {
                if (value <= 0 || value >= InputPorts.Count) return;

                Debug.Console(DebugLevelDebug, this, "SetInput: value-'{0}'", value);

                ExecuteSwitch(InputPorts.ElementAt(value - 1).Selector);
                UpdateInputFb((byte)InputPorts.ElementAt(value - 1).FeedbackMatchObject);
            }
        }

        private bool _isCoolingDown;
        private bool _isMuted;
        private bool _isPoweringOnIgnorePowerFb;
        private bool _isWarmingUp;
        private bool _lastCommandSentWasVolume;
        private int _lastVolumeSent;
        private CCriticalSection _parseLock = new CCriticalSection();
        private CTimer _pollRing;
        private bool _powerIsOn;
        private ActionIncrementer _volumeIncrementer;
        private bool _volumeIsRamping;
        private ushort _volumeLevelForSig;

		private readonly bool _showVolumeControls;

        /// <summary>
        /// Constructor for IBaseCommunication
        /// </summary>
        /// <param name="name"></param>
        /// <param name="config"></param>
        /// <param name="key"></param>
        /// <param name="comms"></param>
        public SamsungMdcDisplayController(string key, string name, SamsungMDCDisplayPropertiesConfig config,
            IBasicCommunication comms)
            : base(key, name)
        {
            Communication = comms;
            Communication.BytesReceived += Communication_BytesReceived;
            _config = config;

            Id = _config.Id == null ? (byte) 0x01 : Convert.ToByte(_config.Id, 16);

            _upperLimit = _config.volumeUpperLimit;
            _lowerLimit = _config.volumeLowerLimit;
            _pollIntervalMs = _config.pollIntervalMs;
            _coolingTimeMs = _config.coolingTimeMs;
            _warmingTimeMs = _config.warmingTimeMs;
			_showVolumeControls = _config.showVolumeControls;
            _pollLedTemps = config.pollLedTemps;

            ResetDebugLevels();

            DeviceInfo = new DeviceInfo();
            Init();
        }

        public IBasicCommunication Communication { get; private set; }
        public byte Id { get; private set; }
        public IntFeedback StatusFeedback { get; set; }        

        private bool ScaleVolume { get; set; }


        protected override Func<bool> PowerIsOnFeedbackFunc
        {
            get { return () => _powerIsOn; }
        }

        protected override Func<bool> IsCoolingDownFeedbackFunc
        {
            get { return () => _isCoolingDown; }
        }

        protected override Func<bool> IsWarmingUpFeedbackFunc
        {
            get { return () => _isWarmingUp; }
        }

        protected override Func<string> CurrentInputFeedbackFunc
        {
            get { return () => _currentInputPort.Key; }
        }

        /// <summary>
        /// 
        /// </summary>
        public override FeedbackCollection<Feedback> Feedbacks
        {
            get
            {
                var list = base.Feedbacks;
                list.AddRange(new List<Feedback>
                {
                    VolumeLevelFeedback,
                    MuteFeedback,
                    CurrentInputFeedback,
                    CurrentLedTemperatureCelsiusFeedback,
                    CurrentLedTemperatureFahrenheitFeedback
                });
                return list;
            }
        }

        #region Command Constants

        /// <summary>
        /// Header byte
        /// </summary>
        public const byte Header = 0xAA;

        /// <summary>
        /// Status control (Cmd: 0x00) pdf page 26
        /// Gets the current status, status includes: val1=Power, val2=Volume, val3=Mute, val4=Input, val5=Aspect, val6=N Time NF, val7=F Time NF
        /// </summary>
        public const byte StatusControlCmd = 0x00;

        /// <summary>
        /// Status control data 1 - get
        /// </summary>
        public const byte StatusControlGet = 0x00;

        /// <summary>
        /// Display status control (Cmd: 0x0D) pdf page 34
        /// Gets the display status, status includes: val1=Lamp, val2=Temperature, val3=Bright_Sensor, val4=No_Sync, val5=Current_Temp, val6=Fan
        /// </summary>
        public const byte DisplayStatusControlCmd = 0x0D;

        /// <summary>
        /// Power control (Cmd: 0x11) pdf page 42
        /// Gets/sets the power state
        /// </summary>
        public const byte PowerControlCmd = 0x11;

        /// <summary>
        /// Power control data1 - on 
        /// </summary>
        public const byte PowerControlOn = 0x01;

        /// <summary>
        /// Power control data1 - off
        /// </summary>
        public const byte PowerControlOff = 0x00;

        /// <summary>
        /// Volume level control (Cmd: 0x12) pdf page 44
        /// Gets/sets the volume level
        /// Level range 0d - 100d (0x00 - 0x64)
        /// </summary>
        public const byte VolumeLevelControlCmd = 0x12;

        /// <summary>
        /// Volume mute control (Cmd: 0x13) pdf page 45
        /// Gets/sets the volume mute state
        /// </summary>
        public const byte VolumeMuteControlCmd = 0x13;

        /// <summary>
        /// Volume mute control data1 - on 
        /// </summary>
        public const byte VolumeMuteControlOn = 0x01;

        /// <summary>
        /// Volume mute control data1 - off
        /// </summary>
        public const byte VolumeMuteControlOff = 0x00;

        /// <summary>
        /// Input source control (Cmd: 0x14) pdf page 46
        /// Gets/sets the input state
        /// </summary>
        public const byte InputControlCmd = 0x14;

        /// <summary>
        /// Input source control data1 - S-Video1
        /// </summary>
        public const byte InputControlSvideo1 = 0x04;

        /// <summary>
        /// Input source control data1 - Component1
        /// </summary>
        public const byte InputControlComponent1 = 0x08;

        /// <summary>
        /// Input source control data1 - AV1
        /// </summary>
        public const byte InputControlAv1 = 0x0C;

        /// <summary>
        /// Input source control data1 - AV2
        /// </summary>
        public const byte InputControlAv2 = 0x0D;

        /// <summary>
        /// Input source control data1 - Scart1
        /// </summary>
        public const byte InputControlScart1 = 0x0E;

        /// <summary>
        /// Input source control data1 - DVI1
        /// </summary>
        public const byte InputControlDvi1 = 0x18;

        /// <summary>
        /// Input source control data1 - PC1
        /// </summary>
        public const byte InputControlPc1 = 0x14;

        /// <summary>
        /// Input source control data1 - BNC1
        /// </summary>
        public const byte InputControlBnc1 = 0x1E;

        /// <summary>
        /// Input source control data1 - DVI Video1
        /// </summary>
        public const byte InputControlDviVideo1 = 0x1F;

        /// <summary>
        /// Input source control data1 - HDMI1
        /// </summary>
        public const byte InputControlHdmi1 = 0x21;

        /// <summary>
        /// Input source control data1 - HDMI1 PC
        /// </summary>
        public const byte InputControlHdmi1Pc = 0x22;

        /// <summary>
        /// Input source control data1 - HDMI2
        /// </summary>
        public const byte InputControlHdmi2 = 0x23;

        /// <summary>
        /// Input source control data1 - HDMI2 PC
        /// </summary>
        public const byte InputControlHdmi2Pc = 0x24;

        /// <summary>
        /// Input source control data1 - DisplayPort1
        /// </summary>
        public const byte InputControlDisplayPort1 = 0x25;

        /// <summary>
        /// Input source control data1 - DisplayPort2
        /// </summary>
        public const byte InputControlDisplayPort2 = 0x26;

        /// <summary>
        /// Input source control data1 - DisplayPort3
        /// </summary>
        public const byte InputControlDisplayPort3 = 0x27;

        /// <summary>
        /// Input source control data1 - HDMI3
        /// </summary>
        public const byte InputControlHdmi3 = 0x31;

        /// <summary>
        /// Input source control data1 - HDMI3 PC
        /// </summary>
        public const byte InputControlHdmi3Pc = 0x32;

        /// <summary>
        /// Input source control data1 - HDMI4
        /// </summary>
        public const byte InputControlHdmi4 = 0x33;

        /// <summary>
        /// Input source control data1 - HDMI4 PC
        /// </summary>
        public const byte InputControlHdmi4Pc = 0x34;

        /// <summary>
        /// Input source control data1 - TV1
        /// </summary>
        public const byte InputControlTv1 = 0x40;

        /// <summary>
        /// Input source control data1 - HDBase-T1
        /// </summary>
        public const byte InputControlHdBaseT1 = 0x55;

        /// <summary>
        /// Picture size control (Cmd: 0x15) pdf page 48
        /// Gets/sets the picture size state
        /// </summary>
        public const byte AspectControlCmd = 0x15;

        /// <summary>
        /// Picture Size control data1 - PC 16x9
        /// </summary>
        public const byte AspectControlPc16X9 = 0x10;

        /// <summary>
        /// Picture Size control data1 - PC 4x3
        /// </summary>
        public const byte AspectControlPc4X3 = 0x18;

        /// <summary>
        /// Picture Size control data1 - PC Original
        /// </summary>
        public const byte AspectControlPcOriginal = 0x20;

        /// <summary>
        /// Picture Size control data1 - PC 21x9
        /// </summary>
        public const byte AspectControlPc21X9 = 0x21;

        /// <summary>
        /// Picture Size control data1 - PC Custom
        /// </summary>
        public const byte AspectControlPcCustom = 0x22;

        /// <summary>
        /// Picture Size control data1 - Video Auto Wide
        /// </summary>
        public const byte AspectControlVideoAutoWide = 0x00;

        /// <summary>
        /// Picture Size control data1 - Video 16x9
        /// </summary>
        public const byte AspectControlVideo16X9 = 0x01;

        /// <summary>
        /// Picture Size control data1 - Video Zoom
        /// </summary>
        public const byte AspectControlVideoZoom = 0x04;

        /// <summary>
        /// Picture Size control data1 - Video Zoom1
        /// </summary>
        public const byte AspectControlVideoZoom1 = 0x05;

        /// <summary>
        /// Picture Size control data1 - Video Zoom2
        /// </summary>
        public const byte AspectControlVideoZoom2 = 0x06;

        /// <summary>
        /// Picture Size control data1 - Video Justified
        /// </summary>
        public const byte AspectControlVideoJustified = 0x09;

        /// <summary>
        /// Picture Size control data1 - Video 4x3
        /// </summary>
        public const byte AspectControlVideo4X3 = 0x0B;

        /// <summary>
        /// Picture Size control data1 - Video Wide Fit
        /// </summary>
        public const byte AspectControlVideoWideFit = 0x0C;

        /// <summary>
        /// Picture Size control data1 - Video Custom
        /// </summary>
        public const byte AspectControlVideoCustom = 0x0D;

        /// <summary>
        /// Picture Size control data1 - Video SmartView1
        /// </summary>
        public const byte AspectControlVideoSmartView1 = 0x0E;

        /// <summary>
        /// Picture Size control data1 - Video SmartView2
        /// </summary>
        public const byte AspectControlVideoSmartView2 = 0x0F;

        /// <summary>
        /// Picture Size control data1 - Video Wide Zoom
        /// </summary>
        public const byte AspectControlVideoWideZoom = 0x31;

        /// <summary>
        /// Picture Size control data1 - Video 21x9
        /// </summary>
        public const byte AspectControlVideo21X9 = 0x32;

        /// <summary>
        /// Brightness Control (Cmd: 0x25) pdf page 77
        /// Gets/sets the brightness level
        /// Level range 0d - 100d (0x00 - 0x64)
        /// </summary>
        public const byte BrightnessControlCmd = 0x25;

        /// <summary>
        /// Volume increment/decrement control (Cmd: 0x62) pdf page 122
        /// Set only, increments/decrements the volume level
        /// </summary>
        public const byte VolumeAdjustCmd = 0x62;

        /// <summary>
        /// Volume increment/decrement control data1 - up
        /// </summary>
        public const byte VolumeAdjustUp = 0x00;

        /// <summary>
        /// Volume increment/decrement control data1 - down
        /// </summary>
        public const byte VolumeAdjustDown = 0x01;

        /// <summary>
        /// Temeprature Control (Cmd: 0x85) pdf page 142
        /// Gets/sets the max temp threshold
        /// Temp Range 75C - 124C
        /// </summary>
        public const byte TemerpatureMaxControlCmd = 0x85;

        /// <summary>
        /// Virtual remote control (Cmd: 0xB0) pdf pg. 81
        /// Set only, emulates the IR remote
        /// </summary>
        public const byte VirtualRemoteCmd = 0xB0;

        /// <summary>
        /// Virtual remote control data1 (keyCode) - Menu (0x1A)
        /// </summary>
        public const byte VirtualRemoteMenu = 0x1A;

        /// <summary>
        /// Virtual remote control data1 (keyCode) - Dpad Up (0x60)
        /// </summary>
        public const byte VirtualRemoteUp = 0x60;

        /// <summary>
        /// Virtual remote control data1 (keyCode) - Dpad Down (0x61)
        /// </summary>
        public const byte VirtualRemoteDown = 0x61;

        /// <summary>
        /// Virtual remote control data1 (keyCode) - Dpad Left (0x65)
        /// </summary>
        public const byte VirtualRemoteLeft = 0x65;

        /// <summary>
        /// Virtual remote control data1 (keyCode) - Dpad Right (0x62)
        /// </summary>
        public const byte VirtualRemoteRight = 0x62;

        /// <summary>
        /// Virtual remote control data1 (keyCode) - Dpad Selct (0x68)
        /// </summary>
        public const byte VirtualRemoteSelect = 0x68;

        /// <summary>
        /// Virtual remote control data1 (keyCode) - Exit (0x2D)
        /// </summary>
        public const byte VirtualRemoteExit = 0x2D;

        /// <summary>
        /// Led Product Feature (Cmd: 0xD0) pdg page 221
        /// LED Product Features has a subset of commands available
        /// </summary>
        public const byte LedMonitoringCmd = 0xD0;

        /// <summary>
        /// Monitoring (Sub CMD 0x84, 2.1.D0.84 Monitoring, pdf page 228 or 244)       
        /// Gets LED Product status, status includes: val1=Power&IC, val2=HDBaseT_Status, val3=Temperature, val4=Illuminance, val5=Module1, val6=Module1_LED_Error_Data,.... valN=ModuleX, valN+1=ModuleX_LED_Error_Data\
        /// Temperature range 0C-254C
        /// Illuminance range 0d - 100d (0x00 - 0x64)
        /// </summary>
        public const byte LedMonitoringSubCmd = 0x84;

        public const byte LedMonitoringModule1 = 0x1E;
        public const byte LedMonitoringModule2 = 0x2E;
        public const byte LedMonitoringModule3 = 0x3E;
        public const byte LedMonitoringModule4 = 0x4E;
        public const byte LedMonitoringModule5 = 0x5E;
        public const byte LedMonitoringModule6 = 0x6E;
        public const byte LedMonitoringModule7 = 0x7E;
        public const byte LedMonitoringModule8 = 0x8E;
        public const byte LedMonitoringModule9 = 0x9E;
        public const byte LedMonitoringModule10 = 0xAE;
        public const byte LedMonitoringModule11 = 0xBE;
        public const byte LedMonitoringModule12 = 0xCE;

        #endregion

        #region IBasicVolumeWithFeedback Members

        /// <summary>
        /// Scales the level to the range of the display and sends the command
        /// Volume level control (Cmd: 0x12) pdf page 44
        /// Level range 0d - 100d (0x00 - 0x64)		
        /// Set: [HEADER=0xAA][Cmd=0x12][ID][DATA_LEN=0x01][DATA-1=(Scaled)][CS=0x00]
        /// </summary>
        /// <param name="level"></param>
        public void SetVolume(ushort level)
        {
            int scaled;
            _lastVolumeSent = level;
            if (!ScaleVolume)
            {
                scaled = (int) NumericalHelpers.Scale(level, 0, 65535, 0, 100);
            }
            else
            {
                scaled = (int) NumericalHelpers.Scale(level, 0, 65535, _lowerLimit, _upperLimit);
            }
            // The inputs to Scale ensure that byte won't overflow
            SendBytes(new byte[] {Header, VolumeLevelControlCmd, 0x00, 0x01, Convert.ToByte(scaled), 0x00});
			if (_isMuted)
			{
				MuteOff();
			}
        }

        /// <summary>
        /// Volume level feedback property
        /// </summary>
        public IntFeedback VolumeLevelFeedback { get; private set; }

        /// <summary>
        /// volume mte feedback property
        /// </summary>
        public BoolFeedback MuteFeedback { get; private set; }

        /// <summary>
        /// Mute off (Cmd: 0x13) pdf page 45
        /// Set: [Header=0xAA][Cmd=0x13][ID][DATA_LEN=0x01][DATA-1=0x00][CS=0x00]
        /// </summary>
        public void MuteOff()
        {
            SendBytes(new byte[] {Header, VolumeMuteControlCmd, 0x00, 0x01, VolumeMuteControlOff, 0x00});
        }

        /// <summary>
        /// Mute on (Cmd: 0x13) pdf page 45
        /// Set: [Header=0xAA][Cmd=0x13][ID][DATA_LEN=0x01][DATA-1=0x01][CS=0x00]
        /// </summary>
        public void MuteOn()
        {
            SendBytes(new byte[] {Header, VolumeMuteControlCmd, 0x00, 0x01, VolumeMuteControlOn, 0x00});
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

        #endregion

        #region IBridgeAdvanced Members

        /// <summary>
        /// LinkToApi (bridge method)
        /// </summary>
        /// <param name="trilist"></param>
        /// <param name="joinStart"></param>
        /// <param name="joinMapKey"></param>
        /// <param name="bridge"></param>
        public void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
        {
            var joinMap = new SamsungDisplayControllerJoinMap(joinStart);

            var joinMapSerialized = JoinMapHelper.GetSerializedJoinMapForDevice(joinMapKey);

            if (!string.IsNullOrEmpty(joinMapSerialized))
            {
                joinMap = JsonConvert.DeserializeObject<SamsungDisplayControllerJoinMap>(joinMapSerialized);
            }

            Debug.Console(DebugLevelDebug, "Linking to Trilist '{0}'", trilist.ID.ToString("X"));
            Debug.Console(DebugLevelTrace, "Linking to Display: {0}", Name);

            //trilist.StringInput[joinMap.Name.JoinNumber].StringValue = Name;
            trilist.SetString(joinMap.Name.JoinNumber, Name);

            CommunicationMonitor.IsOnlineFeedback.LinkInputSig(trilist.BooleanInput[joinMap.IsOnline.JoinNumber]);
            StatusFeedback.LinkInputSig(trilist.UShortInput[joinMap.Status.JoinNumber]);
            
            
            // Power Off
            trilist.SetSigTrueAction(joinMap.PowerOff.JoinNumber, PowerOff);
            PowerIsOnFeedback.LinkComplementInputSig(trilist.BooleanInput[joinMap.PowerOff.JoinNumber]);

            // Power On
            trilist.SetSigTrueAction(joinMap.PowerOn.JoinNumber, PowerOn);
            PowerIsOnFeedback.LinkInputSig(trilist.BooleanInput[joinMap.PowerOn.JoinNumber]);


            // Input digitals
            var count = 0;
            foreach (var input in InputPorts)
            {
                var i = input;
                var inputIndex = count;
                trilist.SetSigTrueAction((ushort) (joinMap.InputSelectOffset.JoinNumber + count),
                    () => SetInput = inputIndex + 1);
                
                var friendlyName = _config.FriendlyNames.FirstOrDefault(n => n.InputKey == i.Key);

                if (friendlyName != null)
                {
                    Debug.Console(DebugLevelDebug, this, "Friendly Name found for input {0}: {1}", i.Key, friendlyName.Name);
                }

                var name = friendlyName == null ? i.Key : friendlyName.Name;
                
                trilist.StringInput[(ushort) (joinMap.InputNamesOffset.JoinNumber + count)].StringValue = name;

                InputFeedback[count].LinkInputSig(
                    trilist.BooleanInput[joinMap.InputSelectOffset.JoinNumber + (uint) count]);
                count++;
            }

			// Input Analog
			trilist.SetUShortSigAction(joinMap.InputSelect.JoinNumber, a =>
			{
				if (a == 0)
				{
					PowerOff();
				}
				else if (a > 0 && a < InputPorts.Count)
				{
					SetInput = a + 1;
				}
				else if (a == 102)
				{
					PowerToggle();
				}
				Debug.Console(DebugLevelVerbose, this, "InputChange {0}", a);
			});

            // input analog feedback
            InputNumberFeedback.LinkInputSig(trilist.UShortInput[joinMap.InputSelect.JoinNumber]);
            InputNumberFeedback.LinkInputSig(trilist.UShortInput[joinMap.InputSelect.JoinNumber]);
            CurrentInputFeedback.OutputChange +=
                (sender, args) => Debug.Console(DebugLevelDebug, "CurrentInputFeedback: {0}", args.StringValue);           			


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
            MuteFeedback.LinkComplementInputSig(trilist.BooleanInput[joinMap.VolumeMuteOff.JoinNumber]);

            // Show Volume Controls
            trilist.SetBool(joinMap.VolumeControlsVisibleFb.JoinNumber, _showVolumeControls);

            // LED temperature analog feedback 
            CurrentLedTemperatureCelsiusFeedback.LinkInputSig(
                trilist.UShortInput[joinMap.LedTemperatureCelsius.JoinNumber]);

            CurrentLedTemperatureCelsiusFeedback.LinkInputSig(
                trilist.UShortInput[joinMap.LedTemperatureCelsius.JoinNumber]); 

            // bridge online change
            trilist.OnlineStatusChange += (sender, args) =>
            {
                if (!args.DeviceOnLine) return;
                
                trilist.SetString(joinMap.Name.JoinNumber, Name);

                PowerIsOnFeedback.FireUpdate();
                CurrentInputFeedback.FireUpdate();

                var port = 0;
                foreach (var inputPort in InputPorts)
                {                    
                    InputFeedback[port].FireUpdate();
                    port++;
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

        #region ICommunicationMonitor Members

        public StatusMonitorBase CommunicationMonitor { get; private set; }

        #endregion
        
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
        /// Initialize 
        /// </summary>
        private void Init()
        {
            WarmupTime = _warmingTimeMs > 0 ? _warmingTimeMs : 15000;
            CooldownTime = _coolingTimeMs > 0 ? _coolingTimeMs : 15000;

            InitCommMonitor();

            InitVolumeControls();

            InitInputPortsAndFeedbacks();

            StatusGet();

            InitTemperatureFeedback();
        }

        private void InitTemperatureFeedback()
        {
            CurrentLedTemperatureCelsiusFeedback = new IntFeedback(() =>
            {
                Debug.Console(DebugLevelVerbose, this, "Current Temperature Celsius {0}", _currentLedTemperatureCelsius);
                return _currentLedTemperatureCelsius;
            });
            CurrentLedTemperatureFahrenheitFeedback = new IntFeedback(() =>
            {
                Debug.Console(DebugLevelVerbose, this, "Current Temperature Fahrenheit {0}", _currentLedTemperatureFahrenheit);
                return _currentLedTemperatureFahrenheit;
            });
        }

        private void InitCommMonitor()
        {
            var pollInterval = _pollIntervalMs > 0 ? _pollIntervalMs : 30000;

            CommunicationMonitor = new GenericCommunicationMonitor(this, Communication, pollInterval, 180000, 300000,
                StatusGet);

            DeviceManager.AddDevice(CommunicationMonitor);

            StatusFeedback = new IntFeedback(() => (int) CommunicationMonitor.Status);

            CommunicationMonitor.StatusChange += (sender, args) =>
            {
                Debug.Console(DebugLevelVerbose, this, "Device status: {0}", CommunicationMonitor.Status);
                StatusFeedback.FireUpdate();
            };
        }

        private void InitInputPortsAndFeedbacks()
        {
            //_InputFeedback = new List<bool>();
            InputFeedback = new List<BoolFeedback>();

            AddRoutingInputPort(
                new RoutingInputPort(RoutingPortNames.HdmiIn1, eRoutingSignalType.Audio | eRoutingSignalType.Video,
                    eRoutingPortConnectionType.Hdmi, new Action(InputHdmi1), this), InputControlHdmi1);

            AddRoutingInputPort(
                new RoutingInputPort(RoutingPortNames.HdmiIn2, eRoutingSignalType.Audio | eRoutingSignalType.Video,
                    eRoutingPortConnectionType.Hdmi, new Action(InputHdmi2), this), InputControlHdmi2);

            AddRoutingInputPort(
                new RoutingInputPort(RoutingPortNames.HdmiIn3, eRoutingSignalType.Audio | eRoutingSignalType.Video,
                    eRoutingPortConnectionType.Hdmi, new Action(InputHdmi3), this), InputControlHdmi3);

            AddRoutingInputPort(
                new RoutingInputPort(RoutingPortNames.HdmiIn4, eRoutingSignalType.Audio | eRoutingSignalType.Video,
                    eRoutingPortConnectionType.Hdmi, new Action(InputHdmi4), this), InputControlHdmi4);

            AddRoutingInputPort(
                new RoutingInputPort(RoutingPortNames.DisplayPortIn1,
                    eRoutingSignalType.Audio | eRoutingSignalType.Video,
                    eRoutingPortConnectionType.DisplayPort, new Action(InputDisplayPort1), this),
                InputControlDisplayPort1);

            AddRoutingInputPort(
                new RoutingInputPort(RoutingPortNames.DisplayPortIn2,
                    eRoutingSignalType.Audio | eRoutingSignalType.Video,
                    eRoutingPortConnectionType.DisplayPort, new Action(InputDisplayPort2), this),
                InputControlDisplayPort2);

            AddRoutingInputPort(
                new RoutingInputPort(RoutingPortNames.DviIn, eRoutingSignalType.Audio | eRoutingSignalType.Video,
                    eRoutingPortConnectionType.Dvi, new Action(InputDvi1), this), InputControlDvi1);


            for (var i = 0; i < InputPorts.Count; i++)
            {
                var j = i;

                InputFeedback.Add(new BoolFeedback(() => CurrentInputNumber == j + 1));
            }

            InputNumberFeedback = new IntFeedback(() =>
            {
                Debug.Console(DebugLevelVerbose, this, "Change Input number {0}", CurrentInputNumber);
                return CurrentInputNumber;
            });
        }

        private void InitVolumeControls()
        {
            if (_upperLimit != _lowerLimit && _upperLimit > _lowerLimit)
            {
                ScaleVolume = true;
            }

            if (!ScaleVolume)
            {
                _volumeIncrementer = new ActionIncrementer(655, 0, 65535, 800, 80,
                    v => SetVolume((ushort) v),
                    () => _lastVolumeSent);
            }
            else
            {
                var scaleUpper = NumericalHelpers.Scale(_upperLimit, 0, 100, 0, 65535);
                var scaleLower = NumericalHelpers.Scale(_lowerLimit, 0, 100, 0, 65535);

                _volumeIncrementer = new ActionIncrementer(655, (int) scaleLower, (int) scaleUpper, 800, 80,
                    v => SetVolume((ushort) v),
                    () => _lastVolumeSent);
            }

            VolumeLevelFeedback = new IntFeedback(() => _volumeLevelForSig);
            MuteFeedback = new BoolFeedback(() => _isMuted);
        }

        /// <summary>
        /// Custom activate
        /// </summary>
        /// <returns></returns>
        public override bool CustomActivate()
        {
            Communication.Connect();
            CommunicationMonitor.StatusChange +=
                (o, a) => Debug.Console(DebugLevelVerbose, this, "Communication monitor state: {0}", CommunicationMonitor.Status);
            CommunicationMonitor.Start();
            return true;
        }

        /// <summary>
        /// Communication bytes recieved
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
                    Debug.Console(DebugLevelVerbose, this, "Received new bytes:{0}", ComTextHelper.GetEscapedText(newBytes));
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
                        newBytes.CopyTo(message, 0);
                        ParseMessage(message);
                        byte[] clear = {};
                        _incomingBuffer = clear;
                        return;
                    }
                }
                if (newBytes[0] == 0xAA)
                {
                    _incomingBuffer = newBytes;
                    if (Debug.Level == 2)
                    {
                        // This check is here to prevent following string format from building unnecessarily on level 0 or 1
                        Debug.Console(DebugLevelVerbose, this, "Add to buffer:{0}", ComTextHelper.GetEscapedText(_incomingBuffer));
                    }
                }
                else
                {
                    byte[] clear = {};
                    _incomingBuffer = clear;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(Debug.ErrorLogLevel.Warning, String.Format("Exception parsing feedback: {0}", ex.Message));
                Debug.LogError(Debug.ErrorLogLevel.Warning, String.Format("Stack trace: {0}", ex.StackTrace));
            }
        }

        private void ParseMessage(byte[] message)
        {
            // input ack rx: {header}{command}{id}{dataLen}{ack/nak}{r-cmd}{val-1}{checksum}
            // input ack rx: { 0xAA }{ 0xFF  }{id}{ 0x03  }{'A'/'N'}{ 0x14}{input}{checksum}
            var command = message[5];

            if (Debug.Level == 2)
            {
                // This check is here to prevent following string format from building unnecessarily on level 0 or 1
                Debug.Console(DebugLevelVerbose, this, "Add to buffer:{0}", ComTextHelper.GetEscapedText(_incomingBuffer));
            }

            Debug.Console(DebugLevelVerbose, this, "ParseMessage: message[6]-'{0}'", message[6]);

            switch (command)
            {
                // General status
                case StatusControlCmd:
                {
                    //UpdatePowerFB(message[2], message[5]); // "power" can be misrepresented when the display sleeps
                    // Handle the first power on fb when waiting for it.
                    if (_isPoweringOnIgnorePowerFb && message[6] == PowerControlOn)
                    {
                        _isPoweringOnIgnorePowerFb = false;
                    }
                    // Ignore general-status power off messages when powering up
                    // if (!(_isPoweringOnIgnorePowerFb && message[6] == PowerControlOff))
                    UpdatePowerFb(message[6]);
                    UpdateVolumeFb(message[7]);
                    UpdateMuteFb(message[8]);
                    UpdateInputFb(message[9]);
                    if (Debug.Level == 2)
                    {
                        // This check is here to prevent following string format from building unnecessarily on level 0 or 1
                        Debug.Console(DebugLevelVerbose, this, "StatusControlCmd Power{0}, Mute{2}, Volume{1} Input{3}", message[6],
                            message[7], message[8], message[9]);
                    }
                    break;
                }
                // Power status
                case PowerControlCmd:
                {
                    UpdatePowerFb(message[6]);
                    break;
                }
                // Volume level
                case VolumeLevelControlCmd:
                {
                    UpdateVolumeFb(message[6]);
                    break;
                }
                // Volume mute status
                case VolumeMuteControlCmd:
                {                    
                    UpdateMuteFb(message[6]);
                    break;
                }
                // Input status
                case InputControlCmd:
                {
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
                case LedMonitoringCmd:
                {
                    Debug.Console(DebugLevelDebug, this, "LedMonitoringCmd SubCmd{0} DataLen{1}", message[6], message[3]);

                    if (message[6] == LedMonitoringSubCmd)
                    {
                        Debug.Console(DebugLevelDebug, this, "LedMonitoringCmd SubCmd{0} Temperature{1}", message[6], message[9]);
                        UpdateLedTemperatureFb(message[9]);    
                    }                    
                    break;
                }
                //Serial Number
                case 0x0b: 
                {
                    var serialNumber = new byte[18];
                    Array.Copy(message, 6, serialNumber, 0, 18);

                    UpdateSerialNumber(serialNumber);
                    break;
                }
                //firmware version;
                case 0x0E: 
                {
                    var length = message[3];

                    var firmware = new byte[length];

                    Array.Copy(message, 6, firmware, 0, length);

                    UpdateFirmwareVersion(firmware);
                    break;
                }
                //network info
                case 0x1B: 
                {
                    var length = message[3];
                    if (message[4] == 0x82)
                    {
                        var ipAddressInfo = new byte[length - 1];

                        Array.Copy(message, 7, ipAddressInfo, 0, length - 1);

                        UpdateNetworkInfo(ipAddressInfo);
                        break;
                    }

                    if (message[4] == 0x81)
                    {
                        var macInfo = new byte[length - 1];

                        Array.Copy(message, 6, macInfo, 0, length - 1);

                        UpdateMacAddress(macInfo);
                        break;
                    }

                    break;
                }
                default:
                {
                    Debug.Console(DebugLevelDebug, this, "Unknown message: {0}", ComTextHelper.GetEscapedText(message));
                    break;
                }
            }
        }

        private void UpdateMacAddress(byte[] macInfo)
        {
            DeviceInfo.MacAddress = String.Format("{0X2}:{1X2}:{2X2}:{3X2}:{4X2}:{5X2}", macInfo[0], macInfo[1],
                macInfo[2], macInfo[3], macInfo[4], macInfo[5]);

            OnDeviceInfoChange();
        }

        private void UpdateNetworkInfo(byte[] ipAddressInfo)
        {
            var ipAddress = new byte[4];

            Array.Copy(ipAddressInfo, 0, ipAddress, 0, 4);

            DeviceInfo.IpAddress = String.Format("{0}.{1}.{2}.{3}", ipAddress[0], ipAddress[1], ipAddress[2],
                ipAddress[3]);

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

            handler(this, new DeviceInfoEventArgs {DeviceInfo = DeviceInfo});
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

        // <summary>
        // Volume feedback
        // </summary>
        private void UpdateVolumeFb(byte b)
        {
            ushort newVol;
            if (!ScaleVolume)
            {
                newVol = (ushort) NumericalHelpers.Scale(b, 0, 100, 0, 65535);
            }
            else
            {
                newVol = (ushort) NumericalHelpers.Scale(b, _lowerLimit, _upperLimit, 0, 65535);
            }
            if (!_volumeIsRamping)
            {
                _lastVolumeSent = newVol;
            }

            if (newVol == _volumeLevelForSig)
            {
                return;
            }
            _volumeLevelForSig = newVol;
            VolumeLevelFeedback.FireUpdate();
        }

        /// <summary>
        /// Mute feedback
        /// </summary>
        private void UpdateMuteFb(byte b)
        {
			_isMuted = b == 1;

            MuteFeedback.FireUpdate();
        }

        /// <summary>
        /// Input feedback
        /// </summary>
        private void UpdateInputFb(byte b)
        {
            var newInput = InputPorts.FirstOrDefault(i => i.FeedbackMatchObject.Equals(b));
            if (newInput == null) return;

            _currentInputPort = newInput;
            CurrentInputFeedback.FireUpdate();
            var key = newInput.Key;
            switch (key)
            {
                case "hdmiIn1":
                    CurrentInputNumber = 1;
                    break;
                case "hdmiIn2":
                    CurrentInputNumber = 2;
                    break;
                case "hdmiIn3":
                    CurrentInputNumber = 3;
                    break;
                case "hdmiIn4":
                    CurrentInputNumber = 4;
                    break;
                case "displayPortIn1":
                    CurrentInputNumber = 5;
                    break;
                case "displayPortIn2":
                    CurrentInputNumber = 6;
                    break;
                case "dviIn":
                    CurrentInputNumber = 7;
                    break;
            }
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
            b[b.Length - 1] = (byte) checksum;

            _lastCommandSentWasVolume = b[1] == 0x12;

            Communication.SendBytes(b);
        }

        /// <summary>
        /// Status control (Cmd: 0x00) pdf page 26
        /// Get: [HEADER=0xAA][Cmd=0x00][ID][DATA_LEN=0x00][CS=0x00]
        /// </summary>
        public void StatusGet()
        {
            //SendBytes(new byte[] { Header, StatusControlCmd, 0x00, 0x00, StatusControlGet, 0x00 });
            SendBytes(new byte[] {Header, 0x00, 0x00, 0x00, 0x00});
           
            if(_pollLedTemps) 
                SendBytes(new byte[] {Header, LedMonitoringCmd, Id, 0x01, LedMonitoringSubCmd});
        }

        /// <summary>
        /// Power on (Cmd: 0x11) pdf page 42 
        /// Set: [HEADER=0xAA][Cmd=0x11][ID][DATA_LEN=0x01][DATA-1=0x01][CS=0x00]
        /// </summary>
        public override void PowerOn()
        {
            _isPoweringOnIgnorePowerFb = true;
            SendBytes(new byte[] {Header, PowerControlCmd, 0x00, 0x01, PowerControlOn, 0x00});

            if (PowerIsOnFeedback.BoolValue || _isWarmingUp || _isCoolingDown)
            {
                return;
            }
            _isWarmingUp = true;
            IsWarmingUpFeedback.FireUpdate();
            // Fake power-up cycle
            WarmupTimer = new CTimer(o =>
            {
                _isWarmingUp = false;
                _powerIsOn = true;
                IsWarmingUpFeedback.FireUpdate();
                PowerIsOnFeedback.FireUpdate();
            }, WarmupTime);
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
            if (!_isWarmingUp && !_isCoolingDown) // PowerIsOnFeedback.BoolValue &&
            {
                SendBytes(new byte[] {Header, PowerControlCmd, 0x00, 0x01, PowerControlOff, 0x00});
                _isCoolingDown = true;
                _powerIsOn = false;
                CurrentInputNumber = 0;
                
                InputNumberFeedback.FireUpdate();
                PowerIsOnFeedback.FireUpdate();
                IsCoolingDownFeedback.FireUpdate();
                // Fake cool-down cycle
                CooldownTimer = new CTimer(o =>
                {
                    _isCoolingDown = false;
                    IsCoolingDownFeedback.FireUpdate();
                }, CooldownTime);
            }
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
                Debug.Console(DebugLevelTrace, this, "Exception Here - {0}", e.Message);
            }
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
            SendBytes(new byte[] {Header, PowerControlCmd, 0x00, 0x00, 0x00});
        }

        /// <summary>
        /// Input HDMI 1 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x21][CS=0x00]
        /// </summary>
        public void InputHdmi1()
        {
            SendBytes(new byte[] {Header, InputControlCmd, 0x00, 0x01, InputControlHdmi1, 0x00});
        }

        /// <summary>
        /// Input HDMI 2 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x23][CS=0x00]
        /// </summary>
        public void InputHdmi2()
        {
            SendBytes(new byte[] {Header, InputControlCmd, 0x00, 0x01, InputControlHdmi2, 0x00});
        }

        /// <summary>
        /// Input HDMI 3 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x31][CS=0x00]
        /// </summary>
        public void InputHdmi3()
        {
            SendBytes(new byte[] {Header, InputControlCmd, 0x00, 0x01, InputControlHdmi3, 0x00});
        }

        /// <summary>
        /// Input HDMI 4 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x33][CS=0x00]
        /// </summary>
        public void InputHdmi4()
        {
            SendBytes(new byte[] {Header, InputControlCmd, 0x00, 0x01, InputControlHdmi4, 0x00});
        }

        /// <summary>
        /// Input DisplayPort 1 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x25][CS=0x00]
        /// </summary>
        public void InputDisplayPort1()
        {
            SendBytes(new byte[] {Header, InputControlCmd, 0x00, 0x01, InputControlDisplayPort1, 0x00});
        }

        /// <summary>
        /// Input DisplayPort 2 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x26][CS=0x00]
        /// </summary>
        public void InputDisplayPort2()
        {
            SendBytes(new byte[] {Header, InputControlCmd, 0x00, 0x01, InputControlDisplayPort2, 0x00});
        }

        /// <summary>
        /// Input DVI 1 (Cmd: 0x14) pdf page 426
        /// Set: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x01][DATA-1=0x18][CS=0x00]
        /// </summary>
        public void InputDvi1()
        {
            SendBytes(new byte[] {Header, InputControlCmd, 0x00, 0x01, InputControlDvi1, 0x00});
        }

        /// <summary>
        /// Input HDMI 1 (Cmd: 0x14) pdf page 426
        /// Get: [HEADER=0xAA][Cmd=0x14][ID][DATA_LEN=0x00][CS=0x00]
        /// </summary>
        public void InputGet()
        {
            SendBytes(new byte[] {Header, InputControlCmd, 0x00, 0x00, 0x00});

            _pollRing = null;
            //PollRing = new CTimer(o => VolumeGet(), null, 1000);
            _pollRing = new CTimer(o => LedProductMonitorGet(), null, 10000);
        }

        /// <summary>
        /// Temeprature Control (Cmd: 0x85) pdf page 142
        /// Get: [HEADER=0xAA][Cmd=0x85][ID][DATA_LEN=0x00][CS=0x00]		
        /// </summary>
        public void TemperatureMaxGet()
        {
            SendBytes(new byte[] {Header, TemerpatureMaxControlCmd, 0x00, 0x00, 0x00});
        }

        /// <summary>
        /// LED Product (Cmd: 0xD0) pdf page 221
        /// LED Product temperature (subcmd: 0x84) pdf page 228
        /// Get: [HHEADER=0xAA][Cmd=0xD0][ID][DATA_LEN=0x01][SUBCMD=0x84][CS=0x00]
        /// </summary>
        public void LedProductMonitorGet()
        {
            SendBytes(new byte[] {Header, LedMonitoringCmd, 0x00, 0x01, LedMonitoringSubCmd, 0x00});
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
            CurrentLedTemperatureFahrenheit = (int) ConvertCelsiusToFahrenheit(temp);
        }

        private double ConvertCelsiusToFahrenheit(double c)
        {            
            var f = ((c*9)/5) + 32;
            Debug.Console(DebugLevelDebug, "ConvertCelsiusToFahrenheit: Fahrenheit = {0}, Celsius = {1}", f, c);

            return f;
        }

        private double ConvertFahrenheitToCelsius(double f)
        {
            var c = (f - 32)*5/9;
            Debug.Console(DebugLevelDebug, "ConvertFahrenheitToCelsius: Fahrenheit = {0}, Celsius = {1}", f, c);

            return c;
        }

        /// <summary>
        /// Executes a switch, turning on display if necessary.
        /// </summary>
        /// <param name="selector"></param>
        public override void ExecuteSwitch(object selector)
        {
            //if (!(selector is Action))
            //    Debug.Console(DebugLevelDebug, this, "WARNING: ExecuteSwitch cannot handle type {0}", selector.GetType());

            if (_powerIsOn)
            {
                var action = selector as Action;
                if (action != null)
                {
                    action();
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
                    var action = selector as Action;
                    if (action != null)
                    {
                        action();
                    }
                };
                IsWarmingUpFeedback.OutputChange += handler; // attach and wait for on FB
                PowerOn();
            }
        }

        /// <summary>
        /// Mute get (Cmd: 0x13) pdf page 45
        /// Get: [Header=0xAA][Cmd=0x13][ID][DATA_LEN=0x00][CS=0x00]
        /// </summary>
        public void MuteGet()
        {
            SendBytes(new byte[] {Header, VolumeMuteControlCmd, 0x00, 0x00, 0x00});
        }

        /// <summary>
        /// Volume level control (Cmd: 0x12) pdf page 44
        /// Level range 0d - 100d (0x00 - 0x64)		
        /// Get: [HEADER=0xAA][Cmd=0x12][ID][DATA_LEN=0x00][CS=0x00]
        /// </summary>
        public void VolumeGet()
        {
            SendBytes(new byte[] {Header, VolumeLevelControlCmd, 0x00, 0x00, 0x00});
            _pollRing = null;
            _pollRing = new CTimer(o => MuteGet(), null, 1000);
        }

        #region Implementation of IDeviceInfoProvider

        public void UpdateDeviceInfo()
        {
            if (DeviceInfo == null)
            {
                DeviceInfo = new DeviceInfo();
            }

            //get serial number
            SendBytes(new byte[] {Header, 0x0B, Id, 0x00});
            //get firmware version
            SendBytes(new byte[] {Header, 0x0E, Id, 0x00});
            //get IP Info
            SendBytes(new byte[] {Header, 0x1b, Id, 0x01, 0x82});
            //get MAC address
            SendBytes(new byte[] {Header, 0x1b, Id, 0x01, 0x81});
        }

        public DeviceInfo DeviceInfo { get; private set; }
        public event DeviceInfoChangeHandler DeviceInfoChanged;

        #endregion

        #region DebugLevels

        private uint DebugLevelTrace { get; set; }
        private uint DebugLevelDebug { get; set; }
        private uint DebugLevelVerbose { get; set; }

        public void ResetDebugLevels()
        {
            DebugLevelTrace = 0;
            DebugLevelDebug = 1;
            DebugLevelVerbose = 2;
        }

        public void SetDebugLevels(uint value)
        {
            DebugLevelTrace = value;
            DebugLevelDebug = value;
            DebugLevelVerbose = value;
        }

        #endregion
    }
}