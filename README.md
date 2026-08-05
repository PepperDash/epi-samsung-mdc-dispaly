![PepperDash Logo](/images/logo_pdt_no_tagline_600.png)
# Samsung MDC Display Plugin

This is a plugin repo for Samsung MDC displays and adds features needed that are not currently part of the Essentials Samsung MDC implementation. To use the plugin follow the steps below.

1. Clone the repo
   - References are managed by nuget. To install the correct references, enter the command below:
     `nuget install .\packages.config -ExcludeVersion -OutputDirectory .\packages`
   - Check the slash direction: if installing from Git Bash, the slashes should be `/` instead of `\`
2. Copy the \*.cplz to th plugin folder created by Essentials
   - ex. \USER\program[X]\plugins\*.cplz
3. Update the Essentials configuration file to include the display objects and display bridge (see examples below)
   - The plugin "type" is set C# to "samsungmdcplugin"
4. Load the Essentials configuration file to the program folder created by Essentials
   - ex. \USER\program[x]\*configurationFile\*.json)
5. Restart Essentials to load the plugin

## Device Specifc Information

This plugin was built using the Samsung SEC-VD-DSW Multiple Display Control document, Ver. 14.4, 2018-4-4.

### RS232 Specification

| Property     | Value |
| ------------ | ----- |
| Baudrate     | 9600  |
| Data Bits    | 8     |
| Parity       | None  |
| Stop Bits    | 1     |
| Flow Control | None  |

### Network Specification

| Property   | Value        |
| ---------- | ------------ |
| Default IP | 192.168.0.10 |
| Port       | 1515         |

### Recommended Samsung OSD Setting

To prevent visible flashing when the input is reasserted, disable the on-screen source info pop-up:

- Menu -> OSD -> Message Display -> Source Info -> Off

This disables the on-screen source pop-up that causes the visible flash when the input is reasserted.

## Example Configuration Object

### Display Object using RS-232

```JSON
{
	"key": "Display01",
	"uid": 1,
	"name": "Front Display",
	"type": "samsungmdcplugin",
	"group": "display",
	"properties": {
		"id": "01",
		"control": {
			"method": "com",
			"controlPortDevKey": "processor",
			"controlPortNumber": 1,
			"comParams": {
				"hardwareHandshake": "None",
				"parity": "None",
				"protocol": "RS232",
				"baudRate": 9600,
				"dataBits": 8,
				"softwareHandshake": "None",
				"stopBits": 1
			}
		},
		"pollIntervalMs": 4500,
		"pollLedTemps": false,
		"coolingTimeMs": 1500,
		"warmingTimeMs": 1500,
		"showVolumeControls": false,
		"volumeUpperLimit": 100,
		"volumeLowerLimit": 0,
		"activeInputs": [
            {
              "key": "hdmi1",
              "name": "HDMI1"
            },
            {
              "key": "hdmi2",
              "name": "HDMI2"
            },
            {
              "key": "displayPort1",
              "name": "Display Port"
            },
            {
              "key": "dvi1",
              "name": "DVI"
            },
            {
              "key": "magicInfo",
              "name": "Magic Info"
            }
          ]
	}
},
```

### Display Object using TCP/IP

```JSON
{
	"key": "Display01",
	"uid": 1,
	"name": "Front Display",
	"type": "samsungmdcplugin",
	"group": "display",
	"properties": {
		"id": "01",
		"control": {
			"method": "tcpIp",
			"tcpSshProperties": {
				"address": "10.0.0.200",
				"port": 1515,
				"username": "",
				"password": "",
				"autoReconnect": true,
				"autoReconnectIntervalMs": 10000
			}
		},
		"pollIntervalMs": 4500,
		"pollLedTemps": false,
		"coolingTimeMs": 1500,
		"warmingTimeMs": 1500,
		"showVolumeControls": false,
		"volumeUpperLimit": 100,
		"volumeLowerLimit": 0,
		"activeInputs": [
            {
              "key": "hdmi1",
              "name": "HDMI1"
            },
            {
              "key": "hdmi2",
              "name": "HDMI2"
            },
            {
              "key": "displayPort1",
              "name": "Display Port"
            },
            {
              "key": "dvi1",
              "name": "DVI"
            },
            {
              "key": "magicInfo",
              "name": "Magic Info"
            }
          ]
	}
},
```

### Display Plugin Bridge Object

```JSON
{
	"key": "eiscBridge-Displays",
	"uid": 4,
	"name": "eiscBridge Displays",
	"group": "api",
	"type": "eiscApi",
	"properties": {
		"control": {
			"method": "ipidTcp",
			"ipid": "B0",
			"tcpSshProperties": {
				"address": "127.0.0.2",
				"port": 0
			}
		},
		"devices": [
			{
				"deviceKey": "Display01",
				"joinStart": 1
			},
			{
				"deviceKey": "Display02",
				"joinStart": 51
			},
			{
				"deviceKey": "Display03",
				"joinStart": 101
			}
		]
	}
}
```

## Bridge Join Map

- Each display has 50 buttons available
- The I/O number will depend on the joinStart defined in the configuration file
  - Add the defined joinStart to the I/O number if not starting at 1

### Digitals

| Input                          | I/O | Output                     |
| ------------------------------ | --- | -------------------------- |
| Power Off                      | 1   | Power Off Fb               |
| Power On                       | 2   | Power On Fb                |
|                                | 3   | Is Two Display Fb          |
| Volume Up                      | 5   |                            |
| Volume Down                    | 6   |                            |
| Volume Mute Toggle             | 7   | Volume Mute On Fb          |
| Input 1 Select [HDMI 1]        | 11  | Input 1 Fb [HDMI 1]        |
| Input 2 Select [HDMI 2]        | 12  | Input 2 Fb [HDMI 2]        |
| Input 3 Select [HDMI 3]        | 13  | Input 3 Fb [HDMI 3]        |
| Input 4 Select [HDMI 4]        | 14  | Input 4 Fb [HDMI 4]        |
| Input 5 Select [DisplayPort 1] | 15  | Input 5 Fb [DisplayPort 1] |
| Input 6 Select [DisplayPort 2] | 16  | Input 6 Fb [DisplayPort 2] |
| Input 7 Select [DVI 1]         | 17  | Input 7 Fb [DVI 1]         |
| Input 8 Select [FUTURE]        | 18  | Input 8 Fb [FUTURE]        |
| Input 9 Select [FUTURE]        | 19  | Input 9 Fb [FUTURE]        |
| Input 10 Select [FUTURE]       | 20  | Input 10 Fb [FUTURE]       |
|                                | 40  | Button 1 Visibility Fb     |
|                                | 41  | Button 2 Visibility Fb     |
|                                | 42  | Button 3 Visibility Fb     |
|                                | 43  | Button 4 Visibility Fb     |
|                                | 44  | Button 5 Visibility Fb     |
|                                | 45  | Button 6 Visibility Fb     |
|                                | 46  | Button 7 Visibility Fb     |
|                                | 47  | Button 8 Visibility Fb     |
|                                | 48  | Button 9 Visibility Fb     |
|                                | 49  | Button 10 Visibility Fb    |
|                                | 50  | Display Online Fb          |

### Analogs

| Input                      | I/O | Output                     |
| -------------------------- | --- | -------------------------- |
| Volume Level Set           | 5   | Volume Level Fb            |
| Input Number Select [1-10] | 11  | Input Number Fb [1-10]     |
|                            |     | LED Temperature Celsius    |
|                            |     | LED Temperature Fahrenheit |

### Serials

| Input | I/O | Output                       |
| ----- | --- | ---------------------------- |
|       | 1   | Display Name                 |
|       | 11  | Input 1 Name [HDMI 1]        |
|       | 12  | Input 2 Name [HDMI 2]        |
|       | 13  | Input 3 Name [HDMI 3]        |
|       | 14  | Input 4 Name [HDMI 4]        |
|       | 15  | Input 5 Name [DisplayPort 1] |
|       | 16  | Input 6 Name [DisplayPort 2] |
|       | 17  | Input 7 Name [DVI 1]         |
|       | 18  | Input 8 Name [FUTURE]        |
|       | 19  | Input 9 Name [FUTURE]        |
|       | 20  | Input 10 Name [FUTURE]       |
