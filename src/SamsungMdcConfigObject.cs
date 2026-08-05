using System.Collections.Generic;
using Newtonsoft.Json;
using PepperDash.Core;

namespace PepperDashPluginSamsungMdcDisplay
{
    public class SamsungMdcDisplayPropertiesConfig
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("volumeUpperLimit")]
        public int VolumeUpperLimit { get; set; }

        [JsonProperty("volumeLowerLimit")]
        public int VolumeLowerLimit { get; set; }

        [JsonProperty("pollIntervalMs")]
        public long PollIntervalMs { get; set; }

        [JsonProperty("coolingTimeMs")]
        public uint CoolingTimeMs { get; set; }

        [JsonProperty("warmingTimeMs")]
        public uint WarmingTimeMs { get; set; }

        [JsonProperty("showVolumeControls")]
        public bool ShowVolumeControls { get; set; }

        [JsonProperty("pollLedTemps")]
        public bool PollLedTemps { get; set; }

        [JsonProperty("customInputs")]
        public List<CustomInput> CustomInputs { get; set; }

        [JsonProperty("activeInputs")]
        public List<ActiveInputs> ActiveInputs { get; set; } 

        public SamsungMdcDisplayPropertiesConfig()
        {
            CustomInputs = new List<CustomInput>();
            ActiveInputs = new List<ActiveInputs>();
        }
    }

    public class CustomInput
    {
        [JsonProperty("inputCommand")]
        public string InputCommand { get; set; }
        [JsonProperty("inputConnector")]
        public string InputConnector { get; set; }
        [JsonProperty("inputIdentifier")]
        public string InputIdentifier { get; set; }
    }

    public class ActiveInputs : IKeyName
    {
        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        public ActiveInputs()
        {
            Key = string.Empty;
            Name = string.Empty;
        }
    }
        

}