import { useEffect, useState, useRef } from 'react';
import { useParams } from 'react-router-dom';
import { getSharedPlaylist } from '../services/api';
import { Play, Pause, Music, SkipBack, SkipForward, Share2, Loader } from 'lucide-react';

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

    const [currentSong, setCurrentSong] = useState<SharedSong | null>(null);
    const [isPlaying, setIsPlaying] = useState(false);
    const [currentTime, setCurrentTime] = useState(0);
    const audioRef = useRef<HTMLAudioElement>(null);

    useEffect(() => {
        if (token) loadPlaylist();
    }, [token]);

    const loadPlaylist = async () => {
        try {
            setLoading(true);
            const res = await getSharedPlaylist(token!);
            setPlaylist(res.data);
        } catch (err: any) {
            setError(err.response?.status === 404
                ? 'This shared playlist is no longer available.'
                : 'Failed to load playlist.');
        } finally {
            setLoading(false);
        }
    };

    const playSong = (song: SharedSong) => {
        setCurrentSong(song);
        setIsPlaying(true);
        setCurrentTime(0);
    };

    const togglePlay = () => {
        if (audioRef.current) {
            isPlaying ? audioRef.current.pause() : audioRef.current.play();
            setIsPlaying(!isPlaying);
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

    const formatTime = (s: number) => {
        if (!isFinite(s)) return '0:00';
        return `${Math.floor(s / 60)}:${Math.floor(s % 60).toString().padStart(2, '0')}`;
    };

    const streamUrl = currentSong && token ? `/api/media/stream/shared/${token}/${currentSong.id}` : '';

    if (loading) {
        return (
            <div className="min-h-screen bg-gradient-to-br from-gray-900 via-black to-gray-900 flex items-center justify-center">
                <Loader size={48} className="animate-spin text-blue-500" />
            </div>
        );
    }

    if (error || !playlist) {
        return (
            <div className="min-h-screen bg-gradient-to-br from-gray-900 via-black to-gray-900 flex items-center justify-center p-4">
                <div className="text-center">
                    <Music size={48} className="text-red-500 mx-auto mb-4" />
                    <h1 className="text-2xl font-bold text-white mb-2">Playlist Not Found</h1>
                    <p className="text-gray-400">{error}</p>
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
                    autoPlay
                    onTimeUpdate={() => setCurrentTime(audioRef.current?.currentTime || 0)}
                    onEnded={playNext}
                    onPlay={() => setIsPlaying(true)}
                    onPause={() => setIsPlaying(false)}
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
                    return (
                        <div
                            key={song.id}
                            onClick={() => playSong(song)}
                            className={`flex items-center gap-4 p-4 rounded-xl cursor-pointer transition ${isActive ? 'bg-blue-600/20' : 'hover:bg-white/5'}`}
                        >
                            <div className="w-10 text-center text-gray-500">{i + 1}</div>
                            <div className="flex-1 min-w-0">
                                <div className={`font-medium truncate ${isActive ? 'text-blue-400' : 'text-white'}`}>{song.title}</div>
                                <div className="text-sm text-gray-500 truncate">{song.artist} • {song.album}</div>
                            </div>
                            <div className="text-sm text-gray-500">{formatTime(song.duration)}</div>
                        </div>
                    );
                })}
            </div>

            {currentSong && (
                <div className="fixed bottom-0 left-0 right-0 bg-black/90 backdrop-blur-xl border-t border-white/10 p-4">
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
