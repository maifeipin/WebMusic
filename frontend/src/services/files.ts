import api from './api';

export interface FileItem {
    name: string;
    type: 'Source' | 'Directory' | 'File';
    path: string;
    sourceId: number;
}

export const browseFiles = async (sourceId?: number, path: string = '') => {
    const params = new URLSearchParams();
    if (sourceId) params.append('sourceId', sourceId.toString());
    if (path) params.append('path', path);

    const { data } = await api.get<FileItem[]>(`/files/browse?${params.toString()}`);
    return data;
};

export const createDirectory = async (sourceId: number, path: string) => {
    await api.post('/files/mkdir', { sourceId, path });
};

export const uploadFile = async (sourceId: number, path: string, file: File, onProgress?: (percent: number) => void) => {
    const formData = new FormData();
    // Path should be directory path
    formData.append('file', file);

    await api.post(`/files/upload?sourceId=${sourceId}&path=${encodeURIComponent(path)}`, formData, {
        headers: {
            'Content-Type': 'multipart/form-data',
        },
        timeout: 3600000, // 1 Hour
        onUploadProgress: (progressEvent) => {
            if (onProgress && progressEvent.total) {
                const percent = Math.round((progressEvent.loaded * 100) / progressEvent.total);
                onProgress(percent);
            }
        }
    });
};
