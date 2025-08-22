using UnityEngine;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Common;

public class MidiOutDemo : MonoBehaviour
{
    // 把名字改成你在 loopMIDI / IAC 里建的端口名
    public string midiOutName = "loopMIDI Port";
    public int channel = 0; // CH1 (0-based)
    OutputDevice outDev;

    void Start()
    {
        outDev = OutputDevice.GetByName(midiOutName);
        if (outDev == null)
        {
            Debug.LogError($"No MIDI Output Device found named '{midiOutName}'");
            return;
        }
        Debug.Log("Opened MIDI Output: " + outDev.Name);

    }

    void OnDestroy() { outDev?.Dispose(); }

    void Update()
    {
        // Demo：按空格触发一个短音，鼠标X改滤波CC(#74)
        if (Input.GetKeyDown(KeyCode.Space))
        {
            SendNote(60, 100, 0.2); // C4, 200ms
        }
        int cc74 = Mathf.RoundToInt(Mathf.InverseLerp(0, Screen.width, Input.mousePosition.x) * 127f);
        SendCC(74, cc74);
    }

    void SendNote(int note, int vel, double durSec)
    {
        outDev.SendEvent(new NoteOnEvent((SevenBitNumber)note, (SevenBitNumber)vel)
        { Channel = (FourBitNumber)channel });

        // 简易延时关音：正式项目可做“按 dspTime 的优先队列调度”（见下）
        Invoke(nameof(NoteOffTemp), (float)durSec);
        _pendingNote = note;
    }
    int _pendingNote = -1;
    void NoteOffTemp()
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
