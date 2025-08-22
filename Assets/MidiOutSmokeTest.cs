using UnityEngine;
using System.Linq; // ����� First() �Ļ���Ҫ
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Common;

public class MidiOutSmokeTest : MonoBehaviour
{
    public string midiOutName = "loopMIDI Port";  // Ϊ�����Զ�ѡ��һ��
    public int channel = 0;          // CH1 (0-based)
    private OutputDevice outDev;     // �� OutputDevice�������� IOutputDevice

    void Start()
    {
        // �г����п��õ� MIDI ����豸
        var devices = OutputDevice.GetAll(); // ICollection<OutputDevice>��û�� []
        foreach (var d in devices)
            Debug.Log("[MIDI OUT] " + d.Name);

        if (devices.Count == 0)
        {
            Debug.LogError("No MIDI output devices found. Start loopMIDI/IAC first.");
            return;
        }

        // ѡ�豸�����Ȱ����ƣ���ΰ�����
        outDev = string.IsNullOrEmpty(midiOutName)
               ? OutputDevice.GetByIndex(0)                          // ֱ�Ӱ�������
               : devices.FirstOrDefault(d => d.Name == midiOutName)  // ��Ҫ System.Linq
                 ?? OutputDevice.GetByIndex(0);

        Debug.Log("Using MIDI OUT: " + outDev.Name);
        outDev.PrepareForEventsSending(); // ��ѡ��Ԥ�ȷ��ͣ������װ�����
    }

    void OnDestroy() { outDev?.Dispose(); }

    void Update()
    {
        if (outDev == null) return;

        // ÿ���һ�� C4��NoteOn/Off������������ Waveform �￴������Ƿ�����
        if (Time.frameCount % 60 == 0)
        {
            SendNote(60, 100, 0.2f);
        }

        // ͬʱ�������� CC74���˲����ã������ڲ��ԡ���ťʽ������
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
