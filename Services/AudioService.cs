using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TagLib;
using Mavino.Models;

namespace Mavino.Services
{
    public class AudioService : IDisposable
    {
        private IWavePlayer? _wavePlayer;
        private AudioFileReader? _audioFile;
        private readonly List<Track> _playlist = new();
        private int _currentIndex = -1;
        private bool _shuffle;
        private RepeatMode _repeatMode = RepeatMode.Off;
        private readonly Random _random = new();
        private List<int> _shuffleOrder = new();
        private int _shufflePos = -1;
        private float _volume = 0.7f;

        public event EventHandler? PlaybackStopped;
        public event EventHandler<Track>? TrackChanged;

        public bool IsPlaying => _wavePlayer?.PlaybackState == PlaybackState.Playing;
        public TimeSpan CurrentTime => _audioFile?.CurrentTime ?? TimeSpan.Zero;
        public TimeSpan TotalTime => _audioFile?.TotalTime ?? TimeSpan.Zero;
        public double Volume
        {
            get => _volume;
            set
            {
                _volume = (float)Math.Clamp(value, 0, 1);
                if (_audioFile != null)
                    _audioFile.Volume = _volume;
            }
        }

        public Track? CurrentTrack => (_currentIndex >= 0 && _currentIndex < _playlist.Count) ? _playlist[_currentIndex] : null;
        public IReadOnlyList<Track> Playlist => _playlist.AsReadOnly();
        public bool Shuffle
        {
            get => _shuffle;
            set
            {
                _shuffle = value;
                if (_shuffle) GenerateShuffleOrder();
            }
        }
        public RepeatMode RepeatMode
        {
            get => _repeatMode;
            set => _repeatMode = value;
        }

        public void LoadFiles(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                if (!System.IO.File.Exists(path)) continue;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is not (".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac")) continue;

                try
                {
                    var track = LoadTrackMetadata(path);
                    _playlist.Add(track);
                }
                catch { }
            }
        }

        public void ClearPlaylist()
        {
            Stop();
            _playlist.Clear();
            _currentIndex = -1;
            _shuffleOrder.Clear();
        }

        private Track LoadTrackMetadata(string path)
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var track = new Track { FilePath = path, Title = fileName, Artist = "Unknown Artist" };

            // Parse "Artist - Title" from filename as baseline
            if (fileName.Contains(" - "))
            {
                var parts = fileName.Split(new[] { " - " }, 2, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    track.Artist = parts[0].Trim();
                    track.Title = parts[1].Trim();
                }
            }

            try
            {
                using var file = TagLib.File.Create(path);
                if (!string.IsNullOrWhiteSpace(file.Tag.Title))
                    track.Title = file.Tag.Title.Trim();
                if (!string.IsNullOrWhiteSpace(file.Tag.FirstPerformer))
                    track.Artist = file.Tag.FirstPerformer.Trim();
                track.Album = string.IsNullOrWhiteSpace(file.Tag.Album) ? "Unknown Album" : file.Tag.Album.Trim();
                track.Duration = file.Properties.Duration;

                // If title still contains "Artist - Title" and artist unknown
                if ((track.Artist == "Unknown Artist" || string.IsNullOrWhiteSpace(track.Artist))
                    && track.Title.Contains(" - "))
                {
                    var parts = track.Title.Split(new[] { " - " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                    {
                        track.Artist = parts[0].Trim();
                        track.Title = parts[1].Trim();
                    }
                }

                if (file.Tag.Pictures.Length > 0)
                {
                    track.AlbumArtData = file.Tag.Pictures[0].Data.Data;
                }
            }
            catch { }

            track.LyricsPath = LrcParser.FindLrcForTrack(path);
            return track;
        }

        public void Play(int index = -1)
        {
            if (_playlist.Count == 0) return;

            if (index >= 0)
            {
                _currentIndex = index;
                if (_shuffle)
                {
                    _shufflePos = _shuffleOrder.IndexOf(index);
                }
            }
            else if (_currentIndex < 0)
            {
                _currentIndex = 0;
            }

            LoadAndPlay(_currentIndex);
        }

        private void LoadAndPlay(int index)
        {
            if (index < 0 || index >= _playlist.Count) return;

            StopInternal();

            var track = _playlist[index];
            try
            {
                _audioFile = new AudioFileReader(track.FilePath);
                _audioFile.Volume = _volume;

                _wavePlayer = new WaveOutEvent();
                _wavePlayer.Init(_audioFile);
                _wavePlayer.PlaybackStopped += OnPlaybackStopped;
                _wavePlayer.Play();

                TrackChanged?.Invoke(this, track);
            }
            catch (Exception)
            {
                // skip bad file
                Next();
            }
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null) return;

            // Natural end
            if (_repeatMode == RepeatMode.One)
            {
                if (_audioFile != null)
                {
                    _audioFile.Position = 0;
                    _wavePlayer?.Play();
                }
            }
            else
            {
                Next(true);
            }
        }

        public void Pause()
        {
            _wavePlayer?.Pause();
        }

        public void Resume()
        {
            _wavePlayer?.Play();
        }

        public void Stop()
        {
            StopInternal();
            _currentIndex = -1;
        }

        private void StopInternal()
        {
            if (_wavePlayer != null)
            {
                _wavePlayer.PlaybackStopped -= OnPlaybackStopped;
                _wavePlayer.Stop();
                _wavePlayer.Dispose();
                _wavePlayer = null;
            }
            _audioFile?.Dispose();
            _audioFile = null;
        }

        public void Seek(TimeSpan position)
        {
            if (_audioFile != null)
            {
                _audioFile.CurrentTime = position;
            }
        }

        public void Next(bool fromEnd = false)
        {
            if (_playlist.Count == 0) return;

            if (_shuffle)
            {
                if (_shuffleOrder.Count == 0) GenerateShuffleOrder();
                _shufflePos++;
                if (_shufflePos >= _shuffleOrder.Count)
                {
                    if (_repeatMode == RepeatMode.All || !fromEnd)
                    {
                        GenerateShuffleOrder();
                        _shufflePos = 0;
                    }
                    else
                    {
                        StopInternal();
                        PlaybackStopped?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                }
                _currentIndex = _shuffleOrder[_shufflePos];
            }
            else
            {
                _currentIndex++;
                if (_currentIndex >= _playlist.Count)
                {
                    if (_repeatMode == RepeatMode.All || !fromEnd)
                        _currentIndex = 0;
                    else
                    {
                        _currentIndex = _playlist.Count - 1;
                        StopInternal();
                        PlaybackStopped?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                }
            }

            LoadAndPlay(_currentIndex);
        }

        public void Previous()
        {
            if (_playlist.Count == 0) return;

            if (CurrentTime.TotalSeconds > 3)
            {
                Seek(TimeSpan.Zero);
                return;
            }

            if (_shuffle)
            {
                if (_shuffleOrder.Count == 0) GenerateShuffleOrder();
                _shufflePos--;
                if (_shufflePos < 0)
                {
                    if (_repeatMode == RepeatMode.All)
                        _shufflePos = _shuffleOrder.Count - 1;
                    else
                        _shufflePos = 0;
                }
                _currentIndex = _shuffleOrder[_shufflePos];
            }
            else
            {
                _currentIndex--;
                if (_currentIndex < 0)
                {
                    if (_repeatMode == RepeatMode.All)
                        _currentIndex = _playlist.Count - 1;
                    else
                        _currentIndex = 0;
                }
            }

            LoadAndPlay(_currentIndex);
        }

        private void GenerateShuffleOrder()
        {
            _shuffleOrder = Enumerable.Range(0, _playlist.Count).OrderBy(_ => _random.Next()).ToList();
            if (_currentIndex >= 0)
            {
                _shufflePos = _shuffleOrder.IndexOf(_currentIndex);
                if (_shufflePos < 0) _shufflePos = 0;
            }
            else
            {
                _shufflePos = -1;
            }
        }

        public void Dispose()
        {
            StopInternal();
        }
    }
}
