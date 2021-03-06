﻿namespace Soundfingerprinting
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	using Soundfingerprinting.Audio.Strides;
	using Soundfingerprinting.Fingerprinting;
	using Soundfingerprinting.Fingerprinting.WorkUnitBuilder;
	using Soundfingerprinting.Hashing;
	
	using Soundfingerprinting.Dao;
	using Soundfingerprinting.Dao.Entities;
	using Soundfingerprinting.DbStorage;
	using Soundfingerprinting.DbStorage.Entities;
	
	using Soundfingerprinting.SoundTools;

	/// <summary>
	/// Singleton class for repository container
	/// </summary>
	public class Repository
	{
		/// <summary>
		///   Min hasher
		/// </summary>
		private readonly MinHash minHash;

		/// <summary>
		///   Storage for min-hash permutations
		/// </summary>
		private readonly IPermutations permutations;
		
		private DatabaseService dbService;
		private FingerprintService fingerprintService;
		
		public Repository(IPermutations permutations, DatabaseService dbService, FingerprintService fingerprintService)
		{
			this.permutations = permutations;
			this.minHash = new MinHash(this.permutations);
			this.dbService = dbService;
			this.fingerprintService = fingerprintService;
		}

		public Dictionary<Track, QueryStats> FindSimilarFromAudioFile(
			int lshHashTables,
			int lshGroupsPerKey,
			int thresholdTables,
			WorkUnitParameterObject param) {
			
			// Get fingerprints
			// TODO: Note that this method might return too few samples
			double[][] LogSpectrogram;
			List<bool[]> signatures = fingerprintService.CreateFingerprintsFromAudioFile(param, out LogSpectrogram);

			long elapsedMiliseconds = 0;
			
			// Query the database using Min Hash
			Dictionary<int, QueryStats> allCandidates = QueryFingerprintManager.QueryOneSongMinHash(
				signatures,
				dbService,
				minHash,
				lshHashTables,
				lshGroupsPerKey,
				thresholdTables,
				ref elapsedMiliseconds);

			IEnumerable<int> ids = allCandidates.Select(p => p.Key);
			IList<Track> tracks = dbService.ReadTrackById(ids);

			// Order by Hamming Similarity
			// Using PLINQ
			OrderedParallelQuery<KeyValuePair<int, QueryStats>> order = allCandidates.AsParallel()
				//IOrderedEnumerable<KeyValuePair<int, QueryStats>> order = allCandidates
				.OrderBy((pair) => pair.Value.OrderingValue =
				         pair.Value.HammingDistance / pair.Value.NumberOfTotalTableVotes
				         + 0.4 * pair.Value.MinHammingDistance);
			
			// Join on the ID properties.
			var joined = from o in order
				join track in tracks on o.Key equals track.Id
				select new { track, o.Value };

			Dictionary<Track, QueryStats> stats = joined.ToDictionary(Key => Key.track, Value => Value.Value);
			
			return stats;
		}
		
		public Dictionary<Track, double> FindSimilarFromAudioSamples(
			int lshHashTables,
			int lshGroupsPerKey,
			int thresholdTables,
			WorkUnitParameterObject param) {
			
			// Get fingerprints
			double[][] LogSpectrogram;
			List<bool[]> signatures = fingerprintService.CreateFingerprintsFromAudioSamples(param.AudioSamples, param, out LogSpectrogram);

			long elapsedMiliseconds = 0;
			
			// Query the database using Min Hash
			Dictionary<int, QueryStats> allCandidates = QueryFingerprintManager.QueryOneSongMinHash(
				signatures,
				dbService,
				minHash,
				lshHashTables,
				lshGroupsPerKey,
				thresholdTables,
				ref elapsedMiliseconds);

			IEnumerable<int> ids = allCandidates.Select(p => p.Key);
			IList<Track> tracks = dbService.ReadTrackById(ids);

			// Order by Hamming Similarity
			// Using PLINQ
			//OrderedParallelQuery<KeyValuePair<int, QueryStats>> order = allCandidates.AsParallel()
			IOrderedEnumerable<KeyValuePair<int, QueryStats>> order = allCandidates
				.OrderBy((pair) => pair.Value.OrderingValue =
				         pair.Value.HammingDistance / pair.Value.NumberOfTotalTableVotes
				         + 0.4 * pair.Value.MinHammingDistance);
			
			// Join on the ID properties.
			var joined = from o in order
				join track in tracks on o.Key equals track.Id
				select new { track, o.Value.Similarity };

			Dictionary<Track, double> stats = joined.ToDictionary(Key => Key.track, Value => Value.Similarity);
			
			return stats;
		}
		
		/// <summary>
		/// Insert track into database
		/// </summary>
		/// <param name="track">Track</param>
		/// <param name="hashTables">Number of hash tables (e.g. 25)</param>
		/// <param name="hashKeys">Number of hash keys (e.g. 4)</param>
		/// <param name="param">WorkUnitParameterObject parameters</param>
		public bool InsertTrackInDatabaseUsingSamples(Track track, int hashTables, int hashKeys, WorkUnitParameterObject param, out double[][] logSpectrogram)
		{
			if (dbService.InsertTrack(track)) {
				List<bool[]> images = fingerprintService.CreateFingerprintsFromAudioSamples(param.AudioSamples, param, out logSpectrogram);
				List<Fingerprint> inserted = AssociateFingerprintsToTrack(images, track.Id);
				if (dbService.InsertFingerprint(inserted)) {
					return HashFingerprintsUsingMinHash(inserted, track, hashTables, hashKeys);
				} else {
					return false;
				}
			} else {
				logSpectrogram = null;
				return false;
			}
		}
		
		/// <summary>
		/// Associate fingerprint signatures with a specific track
		/// </summary>
		/// <param name="fingerprintSignatures">Signatures built from one track</param>
		/// <param name="trackId">Track id, which is the parent for this fingerprints</param>
		/// <returns>List of fingerprint entity objects</returns>
		private List<Fingerprint> AssociateFingerprintsToTrack(IEnumerable<bool[]> fingerprintSignatures, int trackId)
		{
			const int FakeId = -1;
			List<Fingerprint> fingers = new List<Fingerprint>();
			int c = 0;
			foreach (bool[] signature in fingerprintSignatures)
			{
				fingers.Add(new Fingerprint(FakeId, signature, trackId, c));
				c++;
			}

			return fingers;
		}
		
		/// <summary>
		/// Hash Fingerprints using Min-Hash algorithm
		/// </summary>
		/// <param name="listOfFingerprintsToHash">List of fingerprints already inserted in the database</param>
		/// <param name="track">Track of the corresponding fingerprints</param>
		/// <param name="hashTables">Number of hash tables (e.g. 25)</param>
		/// <param name="hashKeys">Number of hash keys (e.g. 4)</param>
		private bool HashFingerprintsUsingMinHash(IEnumerable<Fingerprint> listOfFingerprintsToHash, Track track, int hashTables, int hashKeys)
		{
			List<HashBinMinHash> listToInsert = new List<HashBinMinHash>();
			foreach (Fingerprint fingerprint in listOfFingerprintsToHash)
			{
				int[] hashBins = minHash.ComputeMinHashSignature(fingerprint.Signature); //Compute Min Hashes
				Dictionary<int, long> hashTable = minHash.GroupMinHashToLSHBuckets(hashBins, hashTables, hashKeys);
				foreach (KeyValuePair<int, long> item in hashTable)
				{
					HashBinMinHash hash = new HashBinMinHash(-1, item.Value, item.Key, track.Id, fingerprint.Id);
					listToInsert.Add(hash);
				}
			}
			return dbService.InsertHashBin(listToInsert);
		}
		
	}
}