using UnityEngine;
using System.Collections;
using System.Linq;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Common;

/// �������ɡ�ԭ�������ɡ������Э������������������¶�ɵ�����
/// - ����Ƶ�ʣ�Rate���������������
/// - ���ߣ�Pitch�����������Ƶ� + ���� Pitch Bend
/// - ��ɫ��Timbre�������� CC ���ƣ��˲���ֹ/����/ʧ�棩
///
/// �÷����ҵ����� GameObject���� Inspector ��� midiOutName
/// ��Ϊ loopMIDI / IAC ���ܿ����ľ�ȷ���ƣ��������Զ�ȡ��һ������
public class SonificationMelody : MonoBehaviour
{
    [Header("MIDI Output")]
    [Tooltip("����=�Զ�ѡ��һ������豸������ loopMIDI/IAC �ľ�ȷ����")]
    public string midiOutName = "";
    [Range(0, 15)] public int channel = 0; // 0=CH1

    [Header("Harmony & Notes")]
    [Tooltip("������MIDI ���ߣ���C4=60")]
    public int baseNote = 60;
    [Tooltip("��ѡ���̼��ϣ���Ը�������Ĭ��Ϊ����Э�ͶȽϸߵļ��ϡ�")]
    public int[] consonantIntervals = new int[] { 0, 3, 4, 5, 7, 9, 12 }; // m3, M3, P4, P5, M6, 8ve
    [Tooltip("�������¶��ٸ��˶������Ư��")]
    public int octaveRange = 1; // ���¸� 1 ���˶�
    [Tooltip("���ȷ�Χ")]
    public Vector2Int velocityRange = new Vector2Int(70, 110);
    [Tooltip("�������룩������������")]
    public Vector2 noteLengthRange = new Vector2(0.15f, 0.35f);

    [Header("Rate (����Ƶ��)")]
    [Tooltip("��С/��󴥷�������룩")]
    public Vector2 intervalRange = new Vector2(0.25f, 0.8f);
    [Range(0, 1)] public float rate01 = 0.5f; // 0=����1=�죨���������ڲ�ֵ��
    [Tooltip("����ʱ�䶶�����ٷֱȣ�")]
    [Range(0, 0.5f)] public float intervalJitter = 0.1f;

    [Header("Pitch (�������� & ����)")]
    [Tooltip("�����Ƶ���������")]
    [Range(-24, 24)] public int transposeSemitones = 0;
    [Tooltip("Pitch Bend ��Χ��������������ϳ����� Bend Range ����һ��")]
    [Range(1, 24)] public int pitchBendRangeSemitones = 12;
    [Tooltip("�ⲿ����ӳ�䵽 Pitch Bend��-1..+1����0 Ϊ������")]
    [Range(-1f, 1f)] public float pitchBendNorm = 0f;
    [Tooltip("Pitch Bend ƽ����0=����죬1=�ǳ�ƽ����")]
    [Range(0, 0.99f)] public float bendSlew = 0.8f;

    [Header("Timbre (��ɫ���˲�/ʧ��)")]
    [Tooltip("�ⲿ����ӳ�䵽��ɫ��0..1����Խ��Խ����/��ǿʧ�棩")]
    [Range(0f, 1f)] public float timbre01 = 0.5f;
    [Tooltip("��ɫƽ����0=����죬1=�ǳ�ƽ����")]
    [Range(0, 0.99f)] public float timbreSlew = 0.85f;

    [Tooltip("�˲���ֹ CC��ͨ�� 74��/ ���� CC��ͨ�� 71��/ ʧ�� CC�������ڲ���� MIDI Learn��")]
    public int ccCutoff = 74, ccResonance = 71, ccDistortion = 20;

    [Header("Runtime")]
    public bool autoStart = true;

    private OutputDevice outDev;
    private Coroutine noteLoopCo;
    private float smoothedTimbre = 0.5f;
    private float smoothedBend = 0f;

    void Start()
    {
        // ���豸
        var devices = OutputDevice.GetAll();
        if (devices.Count == 0)
        {
            Debug.LogError("No MIDI output devices found. Start loopMIDI/IAC first, then restart Unity.");
            return;
        }
        foreach (var d in devices) Debug.Log("[MIDI OUT] " + d.Name);

        // ѡ�豸
        outDev = string.IsNullOrEmpty(midiOutName)
            ? OutputDevice.GetByIndex(0)
            : devices.FirstOrDefault(d => d.Name == midiOutName) ?? OutputDevice.GetByIndex(0);

        Debug.Log("Using MIDI OUT: " + outDev.Name);
        outDev.PrepareForEventsSending();

        if (autoStart) StartMelody();
        // ���� CC / PitchBend ����
        StartCoroutine(ContinuousControllers());
    }

    void OnDestroy() { outDev?.Dispose(); }

    // ���� ���⣺���ڱ�Ľű���ʵʱ������Щ��������š��ť�� ���� //
    public void SetRate01(float x) { rate01 = Mathf.Clamp01(x); }
    public void SetTimbre01(float x) { timbre01 = Mathf.Clamp01(x); }
    // -1..+1����ֵ�����䣬��ֵ�����䣨ȡ���� pitchBendRangeSemitones��
    public void SetPitchBend01(float x) { pitchBendNorm = Mathf.Clamp(x, -1f, 1f); }
    public void SetTranspose(int semis) { transposeSemitones = Mathf.Clamp(semis, -24, 24); }

    public void StartMelody()
    {
        if (noteLoopCo == null) noteLoopCo = StartCoroutine(NoteLoop());
    }
    public void StopMelody()
    {
        if (noteLoopCo != null) { StopCoroutine(noteLoopCo); noteLoopCo = null; }
    }

    // ���� ��ѭ�������Э������ ���� //
    IEnumerator NoteLoop()
    {
        var wait = new WaitForSeconds(0.2f);

        while (true)
        {
            // 1) ���㵱ǰ���������rate01 ����������䵽��С�����
            float baseInterval = Mathf.Lerp(intervalRange.y, intervalRange.x, rate01);
            float jitter = 1f + Random.Range(-intervalJitter, intervalJitter);
            float interval = Mathf.Max(0.05f, baseInterval * jitter);

            // 2) ѡһ��Э������ + ����˶�
            int deg = consonantIntervals[Random.Range(0, consonantIntervals.Length)];
            int oct = Random.Range(-octaveRange, octaveRange + 1) * 12;
            int note = baseNote + transposeSemitones + deg + oct;

            // 3) ����������
            int vel = Random.Range(velocityRange.x, velocityRange.y + 1);
            float dur = Random.Range(noteLengthRange.x, noteLengthRange.y);

            // 4) ����
            SendNoteOn(note, vel);
            // ��ǰ��������
            if (dur > interval * 0.9f) dur = interval * 0.9f;
            yield return new WaitForSeconds(dur);
            SendNoteOff(note);

            // 5) �ȵ���һ�δ���
            float rest = Mathf.Max(0.01f, interval - dur);
            yield return new WaitForSeconds(rest);
        }
    }

    // ���� �������ƣ��˲�/����/ʧ�� + Pitch Bend ���� //
    IEnumerator ContinuousControllers()
    {
        var wait = new WaitForSeconds(1f / 60f); // 60Hz ˢ��
        while (true)
        {
            // ָ��ƽ��
            smoothedTimbre = Mathf.Lerp(smoothedTimbre, timbre01, 1f - timbreSlew);
            smoothedBend = Mathf.Lerp(smoothedBend, pitchBendNorm, 1f - bendSlew);

            // ӳ�䵽 CC��0..127��
            int cutoff = Mathf.RoundToInt(Mathf.Lerp(20f, 127f, smoothedTimbre));
            int reso = Mathf.RoundToInt(Mathf.Lerp(30f, 110f, smoothedTimbre));
            int distort = Mathf.RoundToInt(Mathf.Lerp(0f, 127f, Mathf.Pow(smoothedTimbre, 0.8f)));

            SendCC(ccCutoff, cutoff);
            SendCC(ccResonance, reso);
            SendCC(ccDistortion, distort); // ����ĺϳ��������� CC �� MIDI Learn

            // Pitch Bend��-1..+1 -> 0..16383������ 8192
            int bend14 = Mathf.RoundToInt((smoothedBend * 0.5f + 0.5f) * 16383f);
            SendPitchBend(bend14);

            yield return wait;
        }
    }

    // ���� �����¼� ���� //
    void SendNoteOn(int note, int vel)
    {
        outDev?.SendEvent(new NoteOnEvent((SevenBitNumber)note, (SevenBitNumber)vel)
        { Channel = (FourBitNumber)channel });
    }
    void SendNoteOff(int note)
    {
        outDev?.SendEvent(new NoteOffEvent((SevenBitNumber)note, (SevenBitNumber)0)
        { Channel = (FourBitNumber)channel });
    }
    void SendCC(int cc, int value)
    {
        value = Mathf.Clamp(value, 0, 127);
        outDev?.SendEvent(new ControlChangeEvent((SevenBitNumber)cc, (SevenBitNumber)value)
        { Channel = (FourBitNumber)channel });
    }
    void SendPitchBend(int value014)
    {
        // 0..16383������ 8192
        int clamped = Mathf.Clamp(value014, 0, 16383);
        outDev?.SendEvent(new PitchBendEvent((ushort)clamped)
        {
            Channel = (FourBitNumber)channel
        });
    }
}
