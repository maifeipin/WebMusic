import { createContext, useContext, useState, useEffect, type ReactNode } from 'react';
import * as api from '../services/api';

// Song Interface
export interface Song {
    id: number;
    title: string;
    artist: string;
    album: string;
    genre?: string;
    duration: number;
    filePath?: string;
}

interface PlayerContextType {
    currentSong: Song | null;
    isPlaying: boolean;
    playSong: (song: Song) => void;
    togglePlay: () => void;
    // Navigation
    nextSong: () => void;
    prevSong: () => void;
    playNext: () => void;
    playPrev: () => void;
    // Queue
    queue: Song[];
    currentIndex: number;
    addToQueue: (song: Song) => void;
    playQueue: (songs: Song[], startIndex?: number) => void;
    // Transcoding
    showTranscodePrompt: boolean;
    confirmTranscode: (transcode: boolean) => void;
    transcodeMode: boolean;
    // User
    isFavorite: (id: number) => boolean;
    toggleLike: (id: number) => Promise<void>;
    // Song Update
    updateCurrentSong: (updates: Partial<Song>) => void;

    // Persistence
    restoredTime: number;
    saveProgress: (time: number) => void;
}

const PlayerContext = createContext<PlayerContextType | undefined>(undefined);

export const usePlayer = () => {
    const context = useContext(PlayerContext);
    if (!context) throw new Error("usePlayer must be used within PlayerProvider");
    return context;
};

export function PlayerProvider({ children }: { children: ReactNode }) {
    const [currentSong, setCurrentSong] = useState<Song | null>(null);
    const [queue, setQueue] = useState<Song[]>([]);
    const [currentIndex, setCurrentIndex] = useState<number>(-1);
    const [isPlaying, setIsPlaying] = useState(false);
    const [showTranscodePrompt, setShowTranscodePrompt] = useState(false);
    const [pendingSong, setPendingSong] = useState<Song | null>(null);
    const [transcodeMode, setTranscodeMode] = useState(false);
    const [favorites, setFavorites] = useState<Set<number>>(new Set());
    const [restoredTime, setRestoredTime] = useState(0);

    // Initial Restore
    useEffect(() => {
        const init = async () => {
            // 1. Device ID
            let deviceId = localStorage.getItem('webmusic_device_id');
            if (!deviceId) {
                // Polyfill for randomUUID
                if (typeof crypto !== 'undefined' && crypto.randomUUID) {
                    deviceId = crypto.randomUUID();
                } else {
                    // Simple replacement for non-secure contexts
                    deviceId = 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
                        var r = Math.random() * 16 | 0, v = c === 'x' ? r : (r & 0x3 | 0x8);
                        return v.toString(16);
                    });
                }
                localStorage.setItem('webmusic_device_id', deviceId);
            }

            // 2. Restore State
            const raw = localStorage.getItem('webmusic_player_state');
            if (raw) {
                try {
                    const state = JSON.parse(raw);
                    if (state.songIds && state.songIds.length > 0) {
                        // Fetch full song details
                        const res = await api.getSongsByIds(state.songIds);
                        if (res.data && res.data.length > 0) {
                            setQueue(res.data);
                            // Restore index and time
                            if (state.currentSongId) {
                                const idx = res.data.findIndex((s: Song) => s.id === state.currentSongId);
                                if (idx !== -1) {
                                    setCurrentIndex(idx);
                                    // Important: set currentSong explicitly to trigger UI
                                    setCurrentSong(res.data[idx]);
                                    setRestoredTime(state.currentTime || 0);
                                    // Do NOT auto play
                                    setIsPlaying(false);
                                }
                            } else {
                                setCurrentIndex(0);
                                setCurrentSong(res.data[0]);
                            }
                        }
                    }
                } catch (e) {
                    console.error("Failed to restore player state", e);
                }
            }
        };
        init();
    }, []);

    // Save State on Change
    useEffect(() => {
        if (queue.length === 0) return;

        const state = {
            version: 1,
            songIds: queue.map(s => s.id),
            currentSongId: currentSong?.id,
            currentTime: 0, // Gets updated by GlobalPlayer separately or we assume 0 here
            deviceId: localStorage.getItem('webmusic_device_id'),
            lastUpdated: Date.now()
        };
        // We only save structure here. Time saving needs to be triggered by audio events.
        localStorage.setItem('webmusic_player_state', JSON.stringify(state));
    }, [queue, currentSong]);

    // Helper to update just the time in local storage (called by GlobalPlayer)
    const saveProgress = (time: number) => {
        const raw = localStorage.getItem('webmusic_player_state');
        if (raw) {
            const state = JSON.parse(raw);
            state.currentTime = time;
            state.lastUpdated = Date.now();
            localStorage.setItem('webmusic_player_state', JSON.stringify(state));
        }
    };

    // ... existing loadFavorites ...

    useEffect(() => {
        const loadFavorites = async () => {
            // Assuming api.getToken() exists or a similar check for user authentication
            // If not, 'token' would need to be passed or derived from context/state.
            // For now, we'll assume a placeholder for checking if a user is logged in.
            // If there's no user token, we don't attempt to load favorites.
            const token = localStorage.getItem('webmusic_auth_token'); // Example placeholder
            if (!token) return;

            try {
                const res = await api.getFavoriteIds();
                setFavorites(new Set(res.data));
            } catch (e: any) {
                // Suppress 401 (Unauthorized) errors which are expected for non-logged-in users
                if (e.response?.status !== 401) {
                    console.warn("Failed to load favorites", e);
                }
            }
        };
        loadFavorites();
    }, []);

    // Record History on Song Change
    useEffect(() => {
        if (currentSong && isPlaying) { // Only record if playing
            api.addToHistory(currentSong.id).catch(err => console.error("History log failed", err));
        }
    }, [currentSong?.id]); // Only on ID change

    const isFavorite = (id: number) => favorites.has(id);

    const toggleLike = async (id: number) => {
        try {
            const wasFav = favorites.has(id);
            // Optimistic update
            const nextFavs = new Set(favorites);
            if (wasFav) nextFavs.delete(id);
            else nextFavs.add(id);
            setFavorites(nextFavs);

            await api.toggleFavorite(id);
        } catch (e) {
            console.error("Failed to toggle favorite", e);
            // Revert? simpler to just reload or ignore for MVP
        }
    };

    const playSong = (song: Song) => {
        setRestoredTime(0); // Reset restore time on manual play
        playQueue([song], 0);
    };

    const playQueue = (songs: Song[], startIndex: number = 0) => {
        setRestoredTime(0);
        setQueue(songs);
        setCurrentIndex(startIndex);
        loadSong(songs[startIndex]);
    };

    const addToQueue = (song: Song) => {
        setQueue(prev => [...prev, song]);
    };

    const loadSong = (song: Song) => {
        // 1. Check Format
        const ext = song.filePath ? song.filePath.split('.').pop()?.toLowerCase() : '';
        const supported = ['mp3', 'wav', 'ogg', 'm4a', 'flac', 'aac'];
        // Known problematic: wma, ape, dsf
        const webSafe = supported.includes(ext || '');

        if (webSafe) {
            setTranscodeMode(false);
            setCurrentSong(song);
            setIsPlaying(true);
        } else {
            setPendingSong(song);
            setShowTranscodePrompt(true);
        }
    };

    const confirmTranscode = (shouldTranscode: boolean) => {
        setShowTranscodePrompt(false);
        if (shouldTranscode && pendingSong) {
            setTranscodeMode(true);
            setCurrentSong(pendingSong);
            setIsPlaying(true);
        } else {
            setPendingSong(null);
            setIsPlaying(false);
        }
        setPendingSong(null);
    };

    const updateCurrentSong = (updates: Partial<Song>) => {
        if (currentSong) {
            const updated = { ...currentSong, ...updates };
            setCurrentSong(updated);
            // Also update in queue if present
            setQueue(prev => prev.map(s => s.id === updated.id ? updated : s));
        }
    };

    const togglePlay = () => setIsPlaying(!isPlaying);

    const nextSong = () => {
        if (currentIndex < queue.length - 1) {
            const nextIdx = currentIndex + 1;
            setRestoredTime(0);
            setCurrentIndex(nextIdx);
            loadSong(queue[nextIdx]);
        }
    };

    const prevSong = () => {
        if (currentIndex > 0) {
            const prevIdx = currentIndex - 1;
            setRestoredTime(0);
            setCurrentIndex(prevIdx);
            loadSong(queue[prevIdx]);
        }
    };

    return (
        <PlayerContext.Provider value={{
            currentSong,
            isPlaying,
            playSong,
            togglePlay,
            nextSong,
            prevSong,
            playNext: nextSong, // Alias
            playPrev: prevSong, // Alias
            showTranscodePrompt,
            confirmTranscode,
            transcodeMode,
            playQueue,
            queue,
            currentIndex,
            addToQueue,
            isFavorite,
            toggleLike,
            updateCurrentSong,
            restoredTime, // Exposed for GlobalPlayer
            saveProgress  // Exposed for GlobalPlayer
        }}>
            {children}
        </PlayerContext.Provider>
    );
};
