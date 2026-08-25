using System.Collections.Generic;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Config;

namespace PepperDashPluginSamsungMdcDisplay
{
    public class SamsungMdcControllerFactory : EssentialsPluginDeviceFactory<SamsungMdcDisplayController>
    {
        public SamsungMdcControllerFactory()
        {
            MinimumEssentialsFrameworkVersion = "3.0.0";

            TypeNames = new List<string> { "samsungMdcPlugin" };
        }

        #region Overrides of EssentialsDeviceFactory<SamsungMdcDisplayController>

        public override EssentialsDevice BuildDevice(DeviceConfig dc)
        {
            var comms = CommFactory.CreateCommForDevice(dc);

            if (comms == null)
            {
                Debug.LogError("Unable to create comms for device {0}", dc.Key);
                return null;
            }

            var config = dc.Properties.ToObject<SamsungMdcDisplayPropertiesConfig>();

            if (config != null)
            {
                return new SamsungMdcDisplayController(dc.Key, dc.Name, config, comms);
            }

            Debug.LogError("Unable to deserialize config for device {0}", dc.Key);
            return null;
        }

        #endregion
    }
}