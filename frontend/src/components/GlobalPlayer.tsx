import { useEffect, useRef, useState } from 'react';
import { usePlayer } from '../context/PlayerContext';
import { useAuth } from '../context/AuthContext';
import { Play, Pause, Music, SkipBack, SkipForward, AlertTriangle, Minimize2, Maximize2, Heart, ListPlus, Edit2 } from 'lucide-react';
import AddToPlaylistModal from './AddToPlaylistModal';
import EditSongModal from './EditSongModal';

export default function GlobalPlayer() {
    const { currentSong, isPlaying, togglePlay, showTranscodePrompt, confirmTranscode, transcodeMode, nextSong, prevSong, isFavorite, toggleLike, updateCurrentSong } = usePlayer();
    const { token } = useAuth();
    const audioRef = useRef<HTMLAudioElement>(null);
    const [isMinimized, setIsMinimized] = useState(false);
    const [currentTime, setCurrentTime] = useState(0);
    const [duration, setDuration] = useState(0);
    const [seekOffset, setSeekOffset] = useState(0);
    const [showPlaylistModal, setShowPlaylistModal] = useState(false);
    const [showEditModal, setShowEditModal] = useState(false);

    // Reset state on song change
    useEffect(() => {
        setSeekOffset(0);
        setCurrentTime(0);
        setDuration(currentSong?.duration || 0);
    }, [currentSong?.id, currentSong?.duration]);

    // Media Session API for lock screen / notification bar controls
    useEffect(() => {
        if (!currentSong) return;

        if ('mediaSession' in navigator) {
            // Set metadata for lock screen display
            navigator.mediaSession.metadata = new MediaMetadata({
                title: currentSong.title || 'Unknown',
                artist: currentSong.artist || 'Unknown Artist',
                album: currentSong.album || 'Unknown Album',
                artwork: [
                    // Default artwork - music note icon
                    { src: '/icon-192.svg', sizes: '192x192', type: 'image/svg+xml' },
                    { src: '/icon-512.svg', sizes: '512x512', type: 'image/svg+xml' },
                ]
            });

            // Set action handlers for media controls
            navigator.mediaSession.setActionHandler('play', () => {
                if (!isPlaying) togglePlay();
            });

            navigator.mediaSession.setActionHandler('pause', () => {
                if (isPlaying) togglePlay();
            });

            navigator.mediaSession.setActionHandler('previoustrack', () => {
                prevSong();
            });

            navigator.mediaSession.setActionHandler('nexttrack', () => {
                nextSong();
            });

            // Optional: seek handlers for some devices
            navigator.mediaSession.setActionHandler('seekbackward', () => {
                if (audioRef.current) {
                    audioRef.current.currentTime = Math.max(0, audioRef.current.currentTime - 10);
                }
            });

            navigator.mediaSession.setActionHandler('seekforward', () => {
                if (audioRef.current) {
                    audioRef.current.currentTime = Math.min(
                        audioRef.current.duration || 0,
                        audioRef.current.currentTime + 10
                    );
                }
            });
        }
    }, [currentSong, isPlaying, togglePlay, prevSong, nextSong]);

    // Update media session playback state
    useEffect(() => {
        if ('mediaSession' in navigator) {
            navigator.mediaSession.playbackState = isPlaying ? 'playing' : 'paused';
        }
    }, [isPlaying]);

    // Update media session position state (for progress bar on lock screen)
    useEffect(() => {
        if ('mediaSession' in navigator && 'setPositionState' in navigator.mediaSession) {
            const totalDur = currentSong?.duration || duration || 0;
            if (totalDur > 0) {
                try {
                    navigator.mediaSession.setPositionState({
                        duration: totalDur,
                        playbackRate: 1,
                        position: Math.min(currentTime, totalDur)
                    });
                } catch (e) {
                    // Some browsers may not fully support setPositionState
                }
            }
        }
    }, [currentTime, duration, currentSong?.duration]);



    // Draggable State
    const [position, setPosition] = useState({ x: 0, y: 0 });
    const isDragging = useRef(false);
    const dragStart = useRef({ x: 0, y: 0 });

    useEffect(() => {
        if (audioRef.current) {
            if (isPlaying) audioRef.current.play().catch(e => console.error("Play error:", e));
            else audioRef.current.pause();
        }
    }, [isPlaying, currentSong]);

    const handleMouseMove = (e: MouseEvent) => {
        if (!isDragging.current) return;
        const newX = e.clientX - dragStart.current.x;
        const newY = e.clientY - dragStart.current.y;
        setPosition({ x: newX, y: newY });
    };

    const handleMouseUp = () => {
        isDragging.current = false;
        document.removeEventListener('mousemove', handleMouseMove);
        document.removeEventListener('mouseup', handleMouseUp);
    };

    // Drag Handlers (Panel)
    const handleMouseDown = (e: React.MouseEvent) => {
        if ((e.target as HTMLElement).closest('input[type="range"]')) return; // Don't drag panel when using slider
        isDragging.current = true;
        dragStart.current = { x: e.clientX - position.x, y: e.clientY - position.y };
        document.addEventListener('mousemove', handleMouseMove);
        document.addEventListener('mouseup', handleMouseUp);
    };

    // ... (keep handleMouseMove/Up)

    const handleSeek = (e: React.ChangeEvent<HTMLInputElement>) => {
        const time = parseFloat(e.target.value);
        if (transcodeMode) {
            setSeekOffset(time);
            setCurrentTime(0); // Audio stream will restart from 0
            // The URL will update, causing audio reload
        } else {
            if (audioRef.current) audioRef.current.currentTime = time;
            setCurrentTime(time);
        }
    };

    const formatTime = (time: number) => {
        if (!isFinite(time)) return "0:00";
        const minutes = Math.floor(time / 60);
        const seconds = Math.floor(time % 60);
        return `${minutes}:${seconds.toString().padStart(2, '0')}`;
    };

    // Effective time to display
    const displayTime = transcodeMode ? (seekOffset + currentTime) : currentTime;
    const totalDuration = currentSong?.duration || duration || 0;

    // Stream URL calculation
    const streamUrl = currentSong
        ? `/api/media/stream/${currentSong.id}?access_token=${token || ''}${transcodeMode ? `&transcode=true&startTime=${seekOffset}` : ''}`
        : '';

    return (
        <>
            {currentSong && (
                <div
                    className={`fixed z-50 transition-all duration-300 ${isMinimized ? 'w-64' : 'w-96 md:w-[32rem]'}`}
                    style={{
                        bottom: '2rem',
                        right: '2rem',
                        transform: `translate(${position.x}px, ${position.y}px)`,
                        cursor: isDragging.current ? 'grabbing' : 'default'
                    }}
                >
                    <div className="bg-black/60 backdrop-blur-xl border border-white/10 rounded-2xl shadow-2xl overflow-hidden">

                        {/* Header / Handle */}
                        <div
                            onMouseDown={handleMouseDown}
                            className="h-6 bg-white/5 cursor-grab active:cursor-grabbing flex items-center justify-center hover:bg-white/10 transition"
                        >
                            <div className="w-12 h-1 bg-white/20 rounded-full mt-2" />
                        </div>

                        {/* Content */}
                        <div className="p-4 pt-2">
                            {/* Top Row: Song Info & Controls */}
                            <div className="flex gap-4">
                                <div className={`relative bg-gray-800 rounded-lg overflow-hidden flex-shrink-0 transition-all ${isMinimized ? 'w-10 h-10' : 'w-16 h-16'}`}>
                                    <div className="absolute inset-0 flex items-center justify-center bg-gradient-to-br from-blue-900 to-purple-900">
                                        <Music size={isMinimized ? 16 : 24} className="text-white/50" />
                                    </div>
                                </div>

                                <div className="flex-1 min-w-0 flex flex-col justify-center">
                                    <div className="flex items-start justify-between">
                                        <div className="overflow-hidden flex-1 mr-4">
                                            <div className="flex items-center gap-3">
                                                <h4 className="font-bold text-white truncate leading-tight select-none text-lg">
                                                    {currentSong.title}
                                                </h4>
                                                <button
                                                    onClick={() => toggleLike(currentSong.id)}
                                                    className="hover:scale-110 transition active:scale-95 flex-shrink-0"
                                                    onMouseDown={(e) => e.stopPropagation()}
                                                >
                                                    <Heart
                                                        size={20}
                                                        className={isFavorite(currentSong.id) ? "text-red-500" : "text-gray-400 hover:text-white"}
                                                        fill={isFavorite(currentSong.id) ? "currentColor" : "none"}
                                                    />
                                                </button>
                                                <button
                                                    onClick={() => setShowPlaylistModal(true)}
                                                    className="hover:scale-110 transition active:scale-95 flex-shrink-0 text-gray-400 hover:text-white"
                                                    onMouseDown={(e) => e.stopPropagation()}
                                                    title="Add to Playlist"
                                                >
                                                    <ListPlus size={20} />
                                                </button>
                                                <button
                                                    onClick={() => setShowEditModal(true)}
                                                    className="hover:scale-110 transition active:scale-95 flex-shrink-0 text-gray-400 hover:text-blue-400"
                                                    onMouseDown={(e) => e.stopPropagation()}
                                                    title="Edit Song Info"
                                                >
                                                    <Edit2 size={18} />
                                                </button>
                                            </div>
                                            <p className="text-gray-400 text-sm truncate mt-0.5 select-none">{currentSong.artist}</p>
                                        </div>
                                        {/* Minimize Toggle */}
                                        <button
                                            onClick={() => setIsMinimized(!isMinimized)}
                                            className="text-gray-500 hover:text-white p-1 ml-2"
                                            onMouseDown={(e) => e.stopPropagation()}
                                        >
                                            {isMinimized ? <Maximize2 size={14} /> : <Minimize2 size={14} />}
                                        </button>
                                    </div>

                                    {transcodeMode && !isMinimized && (
                                        <span className="text-[10px] text-yellow-500 border border-yellow-500/30 bg-yellow-500/10 px-1.5 py-0.5 rounded w-fit mt-1 select-none">
                                            Transcoding {seekOffset > 0 && `(+${Math.round(seekOffset)}s)`}
                                        </span>
                                    )}
                                </div>
                            </div>

                            {/* Controls */}
                            {!isMinimized && (
                                <div className="mt-4 flex items-center justify-center gap-6">
                                    <button onClick={prevSong} className="text-gray-400 hover:text-white transition hover:scale-110" onMouseDown={(e) => e.stopPropagation()}>
                                        <SkipBack size={20} />
                                    </button>
                                    <button
                                        onClick={togglePlay}
                                        className="bg-white text-black rounded-full p-3 hover:scale-105 transition shadow-lg shadow-white/10"
                                        onMouseDown={(e) => e.stopPropagation()}
                                    >
                                        {isPlaying ? <Pause fill="currentColor" size={24} /> : <Play fill="currentColor" size={24} />}
                                    </button>
                                    <button onClick={nextSong} className="text-gray-400 hover:text-white transition hover:scale-110" onMouseDown={(e) => e.stopPropagation()}>
                                        <SkipForward size={20} />
                                    </button>
                                </div>
                            )}

                            {/* Progress Bar */}
                            {!isMinimized && (
                                <div className="mt-4 space-y-1">
                                    <input
                                        type="range"
                                        min={0}
                                        max={totalDuration}
                                        value={displayTime}
                                        onChange={handleSeek}
                                        className="w-full h-1 bg-white/10 rounded-lg appearance-none cursor-pointer accent-blue-500 hover:accent-blue-400"
                                        onMouseDown={(e) => e.stopPropagation()}
                                    />
                                    <div className="flex justify-between text-[10px] text-gray-500 font-mono">
                                        <span>{formatTime(displayTime)}</span>
                                        <span>{formatTime(totalDuration)}</span>
                                    </div>
                                </div>
                            )}
                        </div>
                    </div>
                </div>
            )}

            <audio
                ref={audioRef}
                src={streamUrl}
                autoPlay={isPlaying}
                onPlay={() => { /* Sync state if needed */ }}
                onPause={() => { /* Sync state if needed */ }}
                onEnded={nextSong}
                onError={(e) => console.error("Audio Load Error", e)}
                onTimeUpdate={(e) => setCurrentTime(e.currentTarget.currentTime)}
                onLoadedMetadata={(e) => {
                    const audio = e.currentTarget;
                    if (!transcodeMode && audio.duration && isFinite(audio.duration)) {
                        setDuration(audio.duration);
                    }
                }}
            />

            {/* Transcode Prompt Modal */}
            {showTranscodePrompt && (
                <div className="fixed inset-0 bg-black/80 z-[100] flex items-center justify-center p-4 backdrop-blur-sm">
                    <div className="bg-gray-900 p-6 rounded-2xl max-w-md w-full border border-gray-800 shadow-2xl">
                        <div className="flex items-center gap-3 text-yellow-500 mb-4">
                            <AlertTriangle size={32} />
                            <h2 className="text-xl font-bold text-white">Unsupported Format</h2>
                        </div>
                        <p className="text-gray-300 mb-6 leading-relaxed">
                            This format may not work in your browser. <br />
                            Transcode to MP3 for playback?
                        </p>
                        <div className="flex justify-end gap-3">
                            <button
                                onClick={() => confirmTranscode(false)}
                                className="px-4 py-2 rounded-lg text-gray-400 hover:text-white hover:bg-white/5 transition"
                            >
                                Cancel
                            </button>
                            <button
                                onClick={() => confirmTranscode(true)}
                                className="px-4 py-2 rounded-lg bg-blue-600 hover:bg-blue-500 text-white font-medium shadow-lg shadow-blue-500/20 transition"
                            >
                                Transcode & Play
                            </button>
                        </div>
                    </div>
                </div>
            )}

            <AddToPlaylistModal
                isOpen={showPlaylistModal}
                onClose={() => setShowPlaylistModal(false)}
                songIds={currentSong?.id ? [currentSong.id] : []}
            />

            <EditSongModal
                isOpen={showEditModal}
                onClose={() => setShowEditModal(false)}
                song={currentSong}
                onSaved={(updated) => {
                    updateCurrentSong(updated);
                }}
            />
        </>);
}
