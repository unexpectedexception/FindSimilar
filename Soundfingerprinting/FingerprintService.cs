namespace Soundfingerprinting.Fingerprinting
{
	using System.Collections.Generic;
	using System.Linq;
	using System.Threading.Tasks;

	using Soundfingerprinting.Audio.Models;
	using Soundfingerprinting.Audio.Services;
	using Soundfingerprinting.Audio.Strides;
	using Soundfingerprinting.Fingerprinting.FFT;
	using Soundfingerprinting.Fingerprinting.Wavelets;
	using Soundfingerprinting.Fingerprinting.WorkUnitBuilder;
	using Soundfingerprinting.Fingerprinting.Configuration;

	public class FingerprintService
	{
		public readonly SpectrumService SpectrumService;
		public readonly IWaveletService WaveletService;
		public readonly FingerprintDescriptor FingerprintDescriptor;
		public readonly IAudioService AudioService;

		public FingerprintService(
			IAudioService audioService,
			FingerprintDescriptor fingerprintDescriptor,
			SpectrumService spectrumService,
			IWaveletService waveletService)
		{
			this.SpectrumService = spectrumService;
			this.WaveletService = waveletService;
			this.FingerprintDescriptor = fingerprintDescriptor;
			this.AudioService = audioService;
		}

		public List<bool[]> CreateFingerprintsFromAudioFile(WorkUnitParameterObject param, out double[][] logSpectrogram)
		{
			float[] samples = AudioService.ReadMonoFromFile(
				param.PathToAudioFile,
				param.FingerprintingConfiguration.SampleRate,
				param.MillisecondsToProcess,
				param.StartAtMilliseconds);

			return CreateFingerprintsFromAudioSamples(samples, param, out logSpectrogram);
		}

		public List<bool[]> CreateFingerprintsFromAudioSamples(float[] samples, WorkUnitParameterObject param, out double[][] logSpectrogram)
		{
			IFingerprintingConfiguration configuration = param.FingerprintingConfiguration;
			AudioServiceConfiguration audioServiceConfiguration = new AudioServiceConfiguration
			{
				LogBins = configuration.LogBins,
				LogBase = configuration.LogBase,
				MaxFrequency = configuration.MaxFrequency,
				MinFrequency = configuration.MinFrequency,
				Overlap = configuration.Overlap,
				SampleRate = configuration.SampleRate,
				WdftSize = configuration.WdftSize,
				NormalizeSignal = configuration.NormalizeSignal,
				UseDynamicLogBase = configuration.UseDynamicLogBase
			};

			logSpectrogram = AudioService.CreateLogSpectrogram(
				samples, configuration.WindowFunction, audioServiceConfiguration);
			
			return this.CreateFingerprintsFromLogSpectrum(
				logSpectrogram,
				configuration.Stride,
				configuration.FingerprintLength,
				configuration.Overlap,
				configuration.TopWavelets);
		}

		public List<bool[]> CreateFingerprintsFromLogSpectrum(
			double[][] logarithmizedSpectrum, IStride stride, int fingerprintLength, int overlap, int topWavelets)
		{
			List<double[][]> spectralImages = SpectrumService.CutLogarithmizedSpectrum(
				logarithmizedSpectrum, stride, fingerprintLength, overlap);

			WaveletService.ApplyWaveletTransformInPlace(spectralImages);
			List<bool[]> fingerprints = new List<bool[]>();

			foreach (var spectralImage in spectralImages)
			{
				bool[] image = FingerprintDescriptor.ExtractTopWavelets(spectralImage, topWavelets);
				fingerprints.Add(image);
			}

			return fingerprints;
		}
	}
}