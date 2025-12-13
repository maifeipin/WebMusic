import { useEffect, useState, useRef } from 'react';
import { useParams } from 'react-router-dom';
import { getSharedPlaylist } from '../services/api';
import { Play, Pause, Music, SkipBack, SkipForward, Share2, Loader, Lock, AlertTriangle } from 'lucide-react';

interface SharedSong {
    id: number;
    title: string;
    artist: string;
    album: string;
    duration: number;
}

interface SharedPlaylistData {
    name: string;
    coverArt: string | null;
    shareToken: string;
    songs: SharedSong[];
}

export default function SharedPlaylistPage() {
    const { token } = useParams<{ token: string }>();
    const [playlist, setPlaylist] = useState<SharedPlaylistData | null>(null);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);
    const [isExpired, setIsExpired] = useState(false);

    // Password State
    const [passwordRequired, setPasswordRequired] = useState(false);
    const [passwordInput, setPasswordInput] = useState('');
    const [password, setPassword] = useState<string | undefined>(undefined); // Confirmed password

    const [currentSong, setCurrentSong] = useState<SharedSong | null>(null);
    const [isPlaying, setIsPlaying] = useState(false);
    const [currentTime, setCurrentTime] = useState(0);
    const [restoredTime, setRestoredTime] = useState(0);
    const [playedIds, setPlayedIds] = useState<Set<number>>(new Set()); // Track played songs
    const audioRef = useRef<HTMLAudioElement>(null);


    // Initial playlist load
    useEffect(() => {
        if (token) loadPlaylist(undefined);
    }, [token]);

    // Initial Resume Logic
    useEffect(() => {
        if (playlist && token) {
            const raw = localStorage.getItem(`webmusic_shared_state_${token}`);
            if (raw) {
                try {
                    const state = JSON.parse(raw);
                    // Restore played IDs
                    if (state.playedIds && Array.isArray(state.playedIds)) {
                        setPlayedIds(new Set(state.playedIds));
                    }

                    const song = playlist.songs.find(s => s.id === state.currentSongId);
                    if (song) {
                        setCurrentSong(song);
                        setRestoredTime(state.currentTime || 0);
                        setIsPlaying(false);

                        // Ensure current song is marked as played
                        setPlayedIds(prev => {
                            const next = new Set(prev);
                            next.add(song.id);
                            return next;
                        });
                    }
                } catch (e) {
                    // Failed to restore shared state
                }
            }
        }
    }, [playlist]);

    // Restored Time effect
    useEffect(() => {
        if (restoredTime > 0 && audioRef.current) {
            audioRef.current.currentTime = restoredTime;
        }
    }, [restoredTime, currentSong]);

    // Save State Interval
    useEffect(() => {
        if (!currentSong || !token) return;

        const save = () => {
            const state = {
                currentSongId: currentSong.id,
                currentTime: audioRef.current?.currentTime || 0,
                lastUpdated: Date.now(),
                playedIds: Array.from(playedIds) // Save as array
            };
            localStorage.setItem(`webmusic_shared_state_${token}`, JSON.stringify(state));
        };

        const interval = setInterval(save, 5000);
        return () => {
            clearInterval(interval);
            save();
        };
    }, [currentSong, token, playedIds]); // Add playedIds dependancy

    // ... (keep loadPlaylist / password handle)

    const loadPlaylist = async (pwd?: string) => {
        try {
            setLoading(true);
            setError(null);
            const res = await getSharedPlaylist(token!, pwd);
            setPlaylist(res.data);
            if (pwd) setPassword(pwd); // Save validated password
            setPasswordRequired(false);
        } catch (err: any) {
            const status = err.response?.status;
            if (status === 401 && err.response?.data?.code === 'PASSWORD_REQUIRED') {
                setPasswordRequired(true);
            } else if (status === 410) {
                setIsExpired(true);
                setError('This share link has expired.');
            } else if (status === 404) {
                setError('Playlist not found.');
            } else {
                setError('Failed to load playlist.');
            }
        } finally {
            setLoading(false);
        }
    };

    const handlePasswordSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        loadPlaylist(passwordInput);
    };

    const playSong = (song: SharedSong) => {
        setRestoredTime(0);
        setCurrentSong(song);
        setIsPlaying(true);
        setCurrentTime(0);
        // Mark as played
        setPlayedIds(prev => new Set(prev).add(song.id));
    };

    const togglePlay = async () => {
        if (audioRef.current && currentSong) {
            try {
                if (isPlaying) {
                    audioRef.current.pause();
                    setIsPlaying(false);
                } else {
                    await audioRef.current.play();
                    setIsPlaying(true);
                }
            } catch (e: any) {
                if (e.name !== 'NotSupportedError' && e.name !== 'AbortError') console.error(e);
            }
        }
    };

    const playNext = () => {
        if (!playlist || !currentSong) return;
        const idx = playlist.songs.findIndex(s => s.id === currentSong.id);
        if (idx < playlist.songs.length - 1) playSong(playlist.songs[idx + 1]);
    };

    const playPrev = () => {
        if (!playlist || !currentSong) return;
        const idx = playlist.songs.findIndex(s => s.id === currentSong.id);
        if (idx > 0) playSong(playlist.songs[idx - 1]);
    };

    // Media Session API
    useEffect(() => {
        if (!currentSong) return;

        if ('mediaSession' in navigator) {
            navigator.mediaSession.metadata = new MediaMetadata({
                title: currentSong.title,
                artist: currentSong.artist,
                album: currentSong.album,
                artwork: [
                    { src: '/icon-192.svg', sizes: '192x192', type: 'image/svg+xml' },
                    { src: '/icon-512.svg', sizes: '512x512', type: 'image/svg+xml' }
                ]
            });

            navigator.mediaSession.setActionHandler('play', () => {
                if (audioRef.current) {
                    audioRef.current.play().catch(console.error);
                    setIsPlaying(true);
                }
            });
            navigator.mediaSession.setActionHandler('pause', () => {
                if (audioRef.current) {
                    audioRef.current.pause();
                    setIsPlaying(false);
                }
            });
            navigator.mediaSession.setActionHandler('previoustrack', playPrev);
            navigator.mediaSession.setActionHandler('nexttrack', playNext);

            // Seek handlers
            navigator.mediaSession.setActionHandler('seekbackward', (details) => {
                if (audioRef.current) {
                    audioRef.current.currentTime = Math.max(0, audioRef.current.currentTime - (details.seekOffset || 10));
                }
            });
            navigator.mediaSession.setActionHandler('seekforward', (details) => {
                if (audioRef.current) {
                    audioRef.current.currentTime = Math.min(audioRef.current.duration || 0, audioRef.current.currentTime + (details.seekOffset || 10));
                }
            });
            navigator.mediaSession.setActionHandler('seekto', (details) => {
                if (audioRef.current && details.seekTime !== undefined) {
                    audioRef.current.currentTime = details.seekTime;
                }
            });
        }
    }, [currentSong, playlist]); // Re-bind when song or playlist (next/prev logic) changes

    useEffect(() => {
        if ('mediaSession' in navigator) {
            navigator.mediaSession.playbackState = isPlaying ? 'playing' : 'paused';
        }
    }, [isPlaying]);

    useEffect(() => {
        if ('mediaSession' in navigator && 'setPositionState' in navigator.mediaSession) {
            const duration = currentSong?.duration || audioRef.current?.duration || 0;
            if (duration && isFinite(duration)) {
                try {
                    navigator.mediaSession.setPositionState({
                        duration: duration,
                        playbackRate: 1,
                        position: Math.min(currentTime, duration)
                    });
                } catch { }
            }
        }
    }, [currentTime, currentSong]);

    const formatTime = (s: number) => {
        if (!isFinite(s)) return '0:00';
        return `${Math.floor(s / 60)}:${Math.floor(s % 60).toString().padStart(2, '0')}`;
    };

    const streamUrl = currentSong && token
        ? `/api/media/stream/shared/${token}/${currentSong.id}${password ? `?pwd=${encodeURIComponent(password)}` : ''}`
        : undefined;

    if (loading && !passwordRequired) {
        return (
            <div className="min-h-screen bg-gradient-to-br from-gray-900 via-black to-gray-900 flex items-center justify-center">
                <Loader size={48} className="animate-spin text-blue-500" />
            </div>
        );
    }

    if (isExpired) {
        return (
            <div className="min-h-screen bg-gradient-to-br from-gray-900 via-black to-gray-900 flex items-center justify-center p-4">
                <div className="text-center bg-gray-900 p-8 rounded-2xl border border-gray-800 shadow-2xl max-w-md w-full">
                    <div className="w-16 h-16 bg-red-500/20 rounded-full flex items-center justify-center mx-auto mb-6">
                        <AlertTriangle size={32} className="text-red-500" />
                    </div>
                    <h1 className="text-2xl font-bold text-white mb-2">Link Expired</h1>
                    <p className="text-gray-400">This shared playlist is no longer available.</p>
                </div>
            </div>
        );
    }

    if (passwordRequired) {
        return (
            <div className="min-h-screen bg-gradient-to-br from-gray-900 via-black to-gray-900 flex items-center justify-center p-4">
                <form onSubmit={handlePasswordSubmit} className="bg-gray-900 p-8 rounded-2xl border border-gray-800 shadow-2xl max-w-md w-full">
                    <div className="w-16 h-16 bg-emerald-500/20 rounded-full flex items-center justify-center mx-auto mb-6">
                        <Lock size={32} className="text-emerald-500" />
                    </div>
                    <h1 className="text-2xl font-bold text-white mb-2 text-center">Password Protected</h1>
                    <p className="text-gray-400 text-center mb-6">Please enter the access code to view this playlist.</p>

                    <input
                        type="password"
                        value={passwordInput}
                        onChange={(e) => setPasswordInput(e.target.value)}
                        className="w-full bg-black/50 border border-gray-700 rounded-lg px-4 py-3 text-white focus:outline-none focus:border-emerald-500 transition mb-4 text-center tracking-widest text-lg"
                        placeholder="••••"
                        autoFocus
                    />

                    <button
                        type="submit"
                        className="w-full py-3 bg-emerald-600 hover:bg-emerald-500 text-white rounded-lg font-medium transition"
                    >
                        Unlock Playlist
                    </button>
                    {error && <p className="text-red-500 text-sm text-center mt-4">Invalid password, please try again.</p>}
                </form>
            </div>
        );
    }

    if (error || !playlist) {
        return (
            <div className="min-h-screen bg-gradient-to-br from-gray-900 via-black to-gray-900 flex items-center justify-center p-4">
                <div className="text-center">
                    <Music size={48} className="text-gray-600 mx-auto mb-4" />
                    <h1 className="text-2xl font-bold text-white mb-2">Playlist Not Found</h1>
                    <p className="text-gray-400">{error || 'This link may be invalid.'}</p>
                </div>
            </div>
        );
    }

    return (
        <div className="min-h-screen bg-gradient-to-br from-gray-900 via-black to-gray-900 text-white">
            {currentSong && (
                <audio
                    ref={audioRef}
                    src={streamUrl}
                    autoPlay={isPlaying}
                    onTimeUpdate={() => setCurrentTime(audioRef.current?.currentTime || 0)}
                    onEnded={playNext}
                    onPlay={() => setIsPlaying(true)}
                    onPause={() => setIsPlaying(false)}
                    onError={() => console.error("Audio Error")}
                />
            )}

            <div className="bg-gradient-to-b from-blue-900/50 to-transparent pt-12 pb-8 px-6">
                <div className="max-w-4xl mx-auto flex flex-col md:flex-row items-center gap-6">
                    <div className="w-48 h-48 bg-gradient-to-br from-blue-600 to-purple-700 rounded-2xl shadow-2xl flex items-center justify-center">
                        <Music size={64} className="text-white/50" />
                    </div>
                    <div className="text-center md:text-left">
                        <p className="text-sm text-blue-400 uppercase tracking-wider mb-2">Shared Playlist</p>
                        <h1 className="text-4xl font-bold mb-4">{playlist.name}</h1>
                        <p className="text-gray-400 mb-4">{playlist.songs.length} songs</p>
                        <div className="flex gap-3 justify-center md:justify-start">
                            <button
                                onClick={() => playlist.songs.length > 0 && playSong(playlist.songs[0])}
                                className="px-6 py-3 bg-blue-600 hover:bg-blue-500 rounded-full font-medium flex items-center gap-2"
                            >
                                <Play size={20} fill="currentColor" /> Play All
                            </button>
                            <button
                                onClick={async () => {
                                    try {
                                        await navigator.clipboard.writeText(window.location.href);
                                        alert('✅ Link copied to clipboard!');
                                    } catch {
                                        prompt('Copy this link:', window.location.href);
                                    }
                                }}
                                className="px-6 py-3 bg-white/10 hover:bg-white/20 rounded-full font-medium flex items-center gap-2"
                            >
                                <Share2 size={18} /> Copy Link
                            </button>
                        </div>
                    </div>
                </div>
            </div>

            <div className="max-w-4xl mx-auto px-6 py-8 space-y-1">
                {playlist.songs.map((song, i) => {
                    const isActive = currentSong?.id === song.id;
                    const isPlayed = playedIds.has(song.id);

                    return (
                        <div
                            key={song.id}
                            onClick={() => playSong(song)}
                            className={`flex items-center gap-4 p-4 rounded-xl cursor-pointer transition group ${isActive ? 'bg-blue-600/20' : 'hover:bg-white/5'
                                }`}
                        >
                            <div className={`w-10 text-center text-sm ${isActive ? 'text-blue-400' : (isPlayed ? 'text-gray-600' : 'text-gray-400')}`}>
                                {isActive ? <div className="animate-pulse">▶</div> : (
                                    isPlayed ? '✓' : i + 1
                                )}
                            </div>
                            <div className="flex-1 min-w-0">
                                <div className={`font-medium truncate transition-colors ${isActive ? 'text-blue-400' : (isPlayed ? 'text-gray-500' : 'text-white')
                                    }`}>
                                    {song.title}
                                </div>
                                <div className={`text-sm truncate ${isPlayed ? 'text-gray-600' : 'text-gray-500'}`}>
                                    {song.artist} • {song.album}
                                </div>
                            </div>
                            <div className={`text-sm ${isPlayed ? 'text-gray-700' : 'text-gray-500'}`}>
                                {formatTime(song.duration)}
                            </div>
                        </div>
                    );
                })}
            </div>

            {currentSong && (
                <div className="fixed bottom-0 left-0 right-0 bg-black/90 backdrop-blur-xl border-t border-white/10 p-4 z-50">
                    <div className="max-w-4xl mx-auto flex items-center gap-4">
                        <div className="w-12 h-12 bg-gradient-to-br from-blue-600 to-purple-700 rounded-lg flex items-center justify-center">
                            <Music size={20} className="text-white/70" />
                        </div>
                        <div className="flex-1 min-w-0">
                            <div className="font-medium truncate">{currentSong.title}</div>
                            <div className="text-sm text-gray-500 truncate">{currentSong.artist}</div>
                        </div>
                        <div className="flex items-center gap-2">
                            <button onClick={playPrev} className="p-2 hover:bg-white/10 rounded-full"><SkipBack size={20} /></button>
                            <button onClick={togglePlay} className="p-3 bg-blue-600 hover:bg-blue-500 rounded-full">
                                {isPlaying ? <Pause size={24} fill="currentColor" /> : <Play size={24} fill="currentColor" />}
                            </button>
                            <button onClick={playNext} className="p-2 hover:bg-white/10 rounded-full"><SkipForward size={20} /></button>
                        </div>
                        <div className="hidden md:block text-sm text-gray-500 w-24 text-right">
                            {formatTime(currentTime)} / {formatTime(currentSong.duration)}
                        </div>
                    </div>
                </div>
            )}
            {currentSong && <div className="h-24" />}
        </div>
    );
}
