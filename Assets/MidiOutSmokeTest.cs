using UnityEngine;
using System.Linq; // 如果用 First() 的话需要
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Common;

public class MidiOutSmokeTest : MonoBehaviour
{
    public string midiOutName = "loopMIDI Port";  // 为空则自动选第一个
    public int channel = 0;          // CH1 (0-based)
    private OutputDevice outDev;     // 用 OutputDevice，而不是 IOutputDevice

    void Start()
    {
        // 列出所有可用的 MIDI 输出设备
        var devices = OutputDevice.GetAll(); // ICollection<OutputDevice>，没有 []
        foreach (var d in devices)
            Debug.Log("[MIDI OUT] " + d.Name);

        if (devices.Count == 0)
        {
            Debug.LogError("No MIDI output devices found. Start loopMIDI/IAC first.");
            return;
        }

        // 选设备：优先按名称，其次按索引
        outDev = string.IsNullOrEmpty(midiOutName)
               ? OutputDevice.GetByIndex(0)                          // 直接按索引拿
               : devices.FirstOrDefault(d => d.Name == midiOutName)  // 需要 System.Linq
                 ?? OutputDevice.GetByIndex(0);

        Debug.Log("Using MIDI OUT: " + outDev.Name);
        outDev.PrepareForEventsSending(); // 可选：预热发送，减少首包卡顿
    }

    void OnDestroy() { outDev?.Dispose(); }

    void Update()
    {
        if (outDev == null) return;

        // 每秒打一发 C4（NoteOn/Off），便于你在 Waveform 里看输入灯是否跳动
        if (Time.frameCount % 60 == 0)
        {
            SendNote(60, 100, 0.2f);
        }

        // 同时连续发送 CC74（滤波常用），便于测试“旋钮式”渐变
        float tri = 0.5f + 0.5f * Mathf.Sin(Time.time);
        SendCC(74, Mathf.RoundToInt(tri * 127f));
    }

    void SendNote(int note, int vel, float dur)
    {
        outDev.SendEvent(new NoteOnEvent((SevenBitNumber)note, (SevenBitNumber)vel)
        { Channel = (FourBitNumber)channel });

        Invoke(nameof(NoteOff), dur);
        _pendingNote = note;
    }
    int _pendingNote = -1;
    void NoteOff()
    {
        if (_pendingNote < 0) return;
        outDev.SendEvent(new NoteOffEvent((SevenBitNumber)_pendingNote, (SevenBitNumber)0)
        { Channel = (FourBitNumber)channel });
        _pendingNote = -1;
    }

    void SendCC(int cc, int value)
    {
        value = Mathf.Clamp(value, 0, 127);
        outDev.SendEvent(new ControlChangeEvent((SevenBitNumber)cc, (SevenBitNumber)value)
        { Channel = (FourBitNumber)channel });
    }
}
