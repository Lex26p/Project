/*
 * Dispatcher C15 deterministic Modbus TCP laboratory source.
 *
 * The script creates virtual MQTT devices only. It does not read or write
 * physical Wiren Board inputs, outputs or connected field devices.
 *
 * Scenario values:
 *   0 - normal deterministic ramp
 *   1 - two-level step
 *   2 - numeric boundaries
 *   3 - freeze all exported read-only values
 *   4 - alarm state
 */

defineVirtualDevice("dispatcher-c15-control", {
    title: "Dispatcher C15 - Scenario control",
    cells: {
        Scenario: {
            type: "range",
            value: 0,
            min: 0,
            max: 4,
            forceDefault: true
        },
        Paused: {
            type: "switch",
            value: false,
            forceDefault: true
        },
        Tick: {
            type: "value",
            value: 0,
            readonly: true,
            forceDefault: true
        }
    }
});

defineVirtualDevice("dispatcher-c15-climate", {
    title: "Dispatcher C15 - Unit 10 climate",
    cells: {
        Temperature: {
            type: "value",
            value: 20.0,
            units: "deg C",
            readonly: true,
            forceDefault: true
        },
        Humidity: {
            type: "value",
            value: 40.0,
            units: "%",
            readonly: true,
            forceDefault: true
        },
        Signed32: {
            type: "value",
            value: -100000,
            readonly: true,
            forceDefault: true
        },
        LowWordCounter: {
            type: "value",
            value: 287454020,
            readonly: true,
            forceDefault: true
        },
        ByteSwapped16: {
            type: "value",
            value: 4660,
            readonly: true,
            forceDefault: true
        }
    }
});

defineVirtualDevice("dispatcher-c15-meter", {
    title: "Dispatcher C15 - Unit 11 meter",
    cells: {
        EnergyCounter: {
            type: "value",
            value: 0,
            units: "Wh",
            readonly: true,
            forceDefault: true
        },
        SignedFlow: {
            type: "value",
            value: -50.00,
            units: "m3/h",
            readonly: true,
            forceDefault: true
        },
        LowWordCounter: {
            type: "value",
            value: 1432778632,
            readonly: true,
            forceDefault: true
        }
    }
});

defineVirtualDevice("dispatcher-c15-state", {
    title: "Dispatcher C15 - Unit 12 state",
    cells: {
        Heartbeat: {
            type: "value",
            value: 0,
            readonly: true,
            forceDefault: true
        },
        StatusCode: {
            type: "value",
            value: 1,
            readonly: true,
            forceDefault: true
        },
        AlarmCode: {
            type: "value",
            value: 0,
            readonly: true,
            forceDefault: true
        },
        Setpoint: {
            type: "range",
            value: 25.0,
            min: 0,
            max: 100,
            units: "deg C",
            forceDefault: true
        }
    }
});

var c15Tick = 0;

function setC15Values(
    temperature,
    humidity,
    signed32,
    climateLowWordCounter,
    byteSwapped16,
    energyCounter,
    signedFlow,
    meterLowWordCounter,
    statusCode,
    alarmCode)
{
    dev["dispatcher-c15-climate/Temperature"] = temperature;
    dev["dispatcher-c15-climate/Humidity"] = humidity;
    dev["dispatcher-c15-climate/Signed32"] = signed32;
    dev["dispatcher-c15-climate/LowWordCounter"] = climateLowWordCounter;
    dev["dispatcher-c15-climate/ByteSwapped16"] = byteSwapped16;

    dev["dispatcher-c15-meter/EnergyCounter"] = energyCounter;
    dev["dispatcher-c15-meter/SignedFlow"] = signedFlow;
    dev["dispatcher-c15-meter/LowWordCounter"] = meterLowWordCounter;

    dev["dispatcher-c15-state/Heartbeat"] = c15Tick % 65536;
    dev["dispatcher-c15-state/StatusCode"] = statusCode;
    dev["dispatcher-c15-state/AlarmCode"] = alarmCode;
}

function updateC15Demo()
{
    if (dev["dispatcher-c15-control/Paused"]) {
        return;
    }

    c15Tick += 1;
    dev["dispatcher-c15-control/Tick"] = c15Tick;

    var scenario = Number(dev["dispatcher-c15-control/Scenario"]) || 0;
    if (scenario === 3) {
        return;
    }

    if (scenario === 1) {
        var high = Math.floor(c15Tick / 10) % 2 === 1;
        setC15Values(
            high ? 28.5 : 21.5,
            high ? 75.0 : 35.0,
            high ? 123456789 : -123456789,
            high ? 287454020 : 16909060,
            high ? 4660 : 43981,
            high ? 200000 : 100000,
            high ? 75.25 : -50.50,
            high ? 1432778632 : 287454020,
            1,
            0);
        return;
    }

    if (scenario === 2) {
        var upper = c15Tick % 2 === 0;
        setC15Values(
            upper ? 3276.0 : -3276.0,
            upper ? 6500.0 : 0.0,
            upper ? 2000000000 : -2000000000,
            upper ? 4000000000 : 0,
            upper ? 65530 : 1,
            upper ? 4000000000 : 0,
            upper ? 20000000.00 : -20000000.00,
            upper ? 4000000000 : 0,
            1,
            0);
        return;
    }

    if (scenario === 4) {
        setC15Values(
            95.0,
            99.9,
            123456789,
            287454020,
            4660,
            987654321,
            9999.99,
            1432778632,
            2,
            1001);
        return;
    }

    setC15Values(
        20.0 + (c15Tick % 101) / 10.0,
        40.0 + (c15Tick % 21),
        -100000 + (c15Tick * 123) % 200001,
        287440000 + c15Tick % 10000,
        1000 + c15Tick % 500,
        (c15Tick * 10) % 4000000000,
        -50.00 + (c15Tick % 200) / 100.0,
        1432700000 + c15Tick % 10000,
        1,
        0);
}

updateC15Demo();
setInterval(updateC15Demo, 1000);
