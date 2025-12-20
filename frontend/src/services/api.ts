import axios from 'axios';

export const api = axios.create({
    baseURL: '/api',
});

api.interceptors.request.use((config: any) => {
    const token = localStorage.getItem('token');
    if (token) {
        config.headers.Authorization = `Bearer ${token}`;
    }
    return config;
});

export const login = (username: string, password: string) => api.post('/auth/login', { username, password });
export const changePassword = (data: any) => api.post('/auth/change-password', data);
export const getStats = () => api.get('/media/stats');
export const getFiles = (params: any) => api.get('/media', { params });
export const getSongsByIds = (ids: number[]) => api.post('/media/list/ids', ids);
export const updateMedia = (id: number, data: { title?: string; artist?: string; album?: string; genre?: string; coverArt?: string }) => api.put(`/media/${id}`, data);
export const deleteMedia = (id: number, force = false) => api.delete(`/media/${id}?force=${force}`);
export const getSources = () => api.get('/scan/sources');
export const addSource = (data: any, force = false) => api.post(`/scan/sources?force=${force}`, data);
export const deleteSource = (id: number) => api.delete(`/scan/sources/${id}`);
export const startScan = (id: number) => api.post(`/scan/start/${id}`);
export const getScanStatus = () => api.get('/scan/status');

export const getCredentials = () => api.get('/credentials');
export const addCredential = (data: any) => api.post('/credentials', data);
export const deleteCredential = (id: number) => api.delete(`/credentials/${id}`);
export const testCredential = (data: any) => api.post('/credentials/test', data);
export const getGroups = (groupBy: string) => api.get(`/media/groups?groupBy=${groupBy}`);
export const getDirectory = (path: string) => api.get(`/media/directory?path=${encodeURIComponent(path)}`);
export const browse = (data: { credentialId: number; path?: string }) => api.post('/scan/browse', data);

// User / History / Favorites
export const addToHistory = (id: number) => api.post(`/user/history/${id}`);
export const getHistory = (page = 1, pageSize = 50) => api.get(`/user/history?page=${page}&pageSize=${pageSize}`);
export const toggleFavorite = (id: number) => api.post(`/user/favorite/${id}`);
export const getFavorites = (page = 1, pageSize = 50) => api.get(`/user/favorites?page=${page}&pageSize=${pageSize}`);
export const getFavoriteIds = () => api.get('/user/favorites/ids');
export const getUserStats = () => api.get('/user/stats');
export const exportFavorites = () => api.get('/user/favorites/export', { responseType: 'blob' });
export const importFavorites = (formData: FormData) => api.post('/user/favorites/import', formData, { headers: { 'Content-Type': 'multipart/form-data' } });

// Full Data Backup
export const exportData = () => api.get('/data/export', { responseType: 'blob' });
export const importData = (formData: FormData, mode: number) => api.post(`/data/import?mode=${mode}`, formData, { headers: { 'Content-Type': 'multipart/form-data' } });

// Playlists
export const getPlaylists = (type: 'normal' | 'shared' | 'all' = 'normal') => api.get(`/playlist?type=${type}`);
export const getPlaylist = (id: number) => api.get(`/playlist/${id}`);
export const createPlaylist = (name: string) => api.post('/playlist', { name });
export const deletePlaylist = (id: number) => api.delete(`/playlist/${id}`);
export const updatePlaylist = (id: number, data: { name?: string; coverArt?: string }) => api.put(`/playlist/${id}`, data);
export const addSongsToPlaylist = (id: number, mediaFileIds: number[]) => api.post(`/playlist/${id}/songs`, mediaFileIds);
export const removeSongsFromPlaylist = (id: number, mediaFileIds: number[]) => api.delete(`/playlist/${id}/songs?ids=${mediaFileIds.join(',')}`);

// Sharing
export const sharePlaylist = (id: number, options?: { name?: string; songIds?: number[]; password?: string; expiresInDays?: number }) =>
    api.post(`/playlist/${id}/share`, options || {});
export const revokePlaylistShare = (id: number) => api.delete(`/playlist/${id}/share`);
// Note: getSharedPlaylist doesn't need auth, so we use a plain axios call
export const getSharedPlaylist = (token: string, password?: string) =>
    axios.get(`/api/playlist/shared/${token}${password ? `?password=${encodeURIComponent(password)}` : ''}`);

// Tags
export const suggestTags = (songIds: number[], prompt: string, model = 'gemini-2.0-flash-exp') => api.post('/tags/suggest', { songIds, prompt, model });
export const startBatch = (songIds: number[], prompt: string, model = 'gemini-2.0-flash-lite-preview-02-05') => api.post('/tags/batch/start', { songIds, prompt, model });
export const getBatchStatus = (batchId: string) => api.get(`/tags/batch/${batchId}`);

export interface Lyric {
    id: number;
    content: string; // LRC
    language: string;
    source: string;
    Title?: string;
    Artist?: string;
    version: string;
}

// Lyrics
export const getAiStatus = () => api.get<{ available: boolean }>('/lyrics/status').then(r => r.data);
export const getLyrics = (mediaId: number) => api.get<Lyric>(`/lyrics/${mediaId}`).then(r => r.data);
export const generateLyrics = (mediaId: number, lang?: string, prompt?: string) => api.post<Lyric>(`/lyrics/${mediaId}/generate`, {}, { params: { lang, prompt } }).then(r => r.data);
export const startLyricsBatch = async (songIds: number[], force: boolean = false, language: string = 'en') => {
    const { data } = await api.post('/lyrics/batch/start', { songIds, force, language });
    return data;
};

export const optimizeLyrics = async (lrcContent: string, mediaId?: number) => {
    const { data } = await api.post('/lyrics/optimize', { lrcContent, mediaId });
    return data.content;
};
export const applyTags = (updates: any[]) => api.post('/tags/apply', updates);

export default api;
